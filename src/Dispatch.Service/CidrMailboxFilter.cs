using Dispatch.Core.Configuration;
using Dispatch.Core.Counters;
using Dispatch.Core.Logging;
using Dispatch.Core.Maintenance;
using Dispatch.Core.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Net;
using SmtpServer.Protocol;
using SmtpServer.Storage;
using System.Net;

namespace Dispatch.Service;

/// <summary>
/// Application-layer access control (spec §5.3, §14.2): refuses MAIL FROM when the source IP is outside
/// the configured allow-list, enforces the global max message size at MAIL FROM, and enforces the
/// per-relay size limit at RCPT TO (before DATA) by running the routing engine. Denied attempts are counted
/// and, when <see cref="ILoggingSettings.LogDeniedAsync"/> is enabled, written to relay_log (spec §6.6/§9.2).
/// </summary>
public sealed class CidrMailboxFilter : IMailboxFilter
{
    /// <summary>Session property key holding the SIZE= declared in MAIL FROM (int; 0 if not declared).</summary>
    internal const string DeclaredSizeKey = "Dispatch.DeclaredSize";

    private readonly ConfigCache _config;
    private readonly ICounterRepository _counters;
    private readonly IRelayResolver _routing;
    private readonly IntakeState _intake;
    private readonly ILogRepository _logRepo;
    private readonly ILoggingSettings _loggingSettings;
    private readonly ILogger<CidrMailboxFilter> _log;

    // Memoised CIDR parse — reparsed only when the allow-list changes in the config table (spec §12.5 live).
    private readonly Lock _cidrLock = new();
    private string? _cidrKey;
    private IPNetwork[] _cidrNetworks = [];

    public CidrMailboxFilter(
        ConfigCache config,
        ICounterRepository counters,
        IRelayResolver routing,
        IntakeState intake,
        ILogRepository logRepo,
        ILoggingSettings loggingSettings,
        ILogger<CidrMailboxFilter> log)
    {
        _config = config;
        _counters = counters;
        _routing = routing;
        _intake = intake;
        _logRepo = logRepo;
        _loggingSettings = loggingSettings;
        _log = log;
    }

    private IPNetwork[] AllowedNetworks(string[] cidrs)
    {
        var key = string.Join(",", cidrs);
        lock (_cidrLock)
        {
            if (key == _cidrKey) return _cidrNetworks;
            _cidrNetworks = cidrs
                .Select(c => IPNetwork.TryParse(c, out var n) ? (IPNetwork?)n : null)
                .Where(n => n.HasValue).Select(n => n!.Value).ToArray();
            _cidrKey = key;
            return _cidrNetworks;
        }
    }

    public async Task<bool> CanAcceptFromAsync(
        ISessionContext context, IMailbox from, int size, CancellationToken cancellationToken)
    {
        // Disk back-pressure (spec §14.1): reject when suspended so senders retry; delay when throttled
        // to slow the inbound rate. Checked first — under disk pressure we don't want to do more work.
        switch (_intake.Level)
        {
            case IntakeLevel.Suspended:
                await DenyAsync(context, from.AsAddress(), null,
                    "Intake suspended (spool disk critically low)", cancellationToken);
                _log.LogWarning("Rejecting MAIL FROM {From}: intake suspended (spool disk critically low)",
                    from.AsAddress());
                // Transient 452 (RFC 5321) so senders retry rather than treating it as a permanent failure (spec §14.1).
                throw new SmtpResponseException(
                    new SmtpResponse(SmtpReplyCode.InsufficientStorage, "Insufficient system storage, try again later"));
            case IntakeLevel.Throttled:
                try { await Task.Delay(IntakeState.ThrottleDelay, cancellationToken); }
                catch (OperationCanceledException) { return false; }
                break;
        }

        // Live settings from the config cache (spec §12.5): edits in the web UI apply on the next connection.
        var listener = _config.Listener();

        // Capture the declared SIZE= for the per-relay check at RCPT TO (spec §14.2).
        context.Properties[DeclaredSizeKey] = size;

        if (listener.MaxMessageBytes > 0 && size > listener.MaxMessageBytes)
        {
            await DenyAsync(context, from.AsAddress(), null,
                $"Declared size {size} exceeds global limit {listener.MaxMessageBytes}", cancellationToken);
            _log.LogWarning("Rejecting MAIL FROM {From}: size {Size} exceeds limit {Limit}",
                from.AsAddress(), size, listener.MaxMessageBytes);
            return false;
        }

        var allowed = AllowedNetworks(listener.EffectiveAllowedCidrs);
        var ip = RemoteIp(context);
        if (ip is null || allowed.Length == 0)
            return true;   // no endpoint info or no allow-list configured → allow

        var test = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
        var permitted = allowed.Any(n => n.Contains(test) || n.Contains(ip));

        if (!permitted)
        {
            await DenyAsync(context, from.AsAddress(), null, $"Source IP {ip} not in allow-list", cancellationToken);
            _log.LogWarning("Denied connection from {Ip} (not in allow-list)", ip);
        }

        return permitted;
    }

    public async Task<bool> CanDeliverToAsync(
        ISessionContext context, IMailbox to, IMailbox from, CancellationToken cancellationToken)
    {
        // No declared SIZE= → can't check before DATA; the actual-size check after DATA is the fallback.
        var declaredSize = context.Properties.TryGetValue(DeclaredSizeKey, out var s) && s is int i ? i : 0;
        if (declaredSize <= 0)
            return true;

        // Routing is now resolvable (MAIL FROM + first RCPT TO known): enforce the per-relay ceiling.
        var relay = await _routing.ResolveAsync(from.AsAddress(), [to.AsAddress()], cancellationToken);
        var limit = relay.Config.EffectiveMaxMessageBytes;

        if (limit > 0 && declaredSize > limit)
        {
            await DenyAsync(context, from.AsAddress(), to.AsAddress(),
                $"Declared size {declaredSize} exceeds relay \"{relay.Name}\" limit {limit}", cancellationToken);
            _log.LogWarning(
                "Rejecting RCPT TO {To}: declared size {Size} exceeds relay \"{Relay}\" limit {Limit}",
                to.AsAddress(), declaredSize, relay.Name, limit);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Records a denial: always increments the Denied counter, and (when enabled) writes a Denied relay_log
    /// row so refusals are visible in the message log, not just the dashboard counter. Both are best-effort —
    /// a logging/counter failure must never turn a refusal into an acceptance.
    /// </summary>
    private async Task DenyAsync(ISessionContext context, string from, string? to, string reason, CancellationToken ct)
    {
        try { await _counters.IncrementAsync(0, CounterField.Denied, ct); }
        catch (Exception ex) { _log.LogError(ex, "Denied-counter increment failed"); }

        try
        {
            if (!await _loggingSettings.LogDeniedAsync(ct)) return;
            await _logRepo.InsertAsync(new RelayLogEntry
            {
                Event = "Denied",
                Status = "Denied",
                FromAddress = from,
                FromDomain = Domain(from),
                ToAddresses = to is null ? [] : [to],
                ToDomain = to is null ? "" : Domain(to),
                Error = reason,
                IngestSource = "SMTP",
                SourceIp = RemoteIp(context)?.ToString(),
            }, ct);
        }
        catch (Exception ex) { _log.LogError(ex, "Denied relay_log insert failed (refusal unaffected)"); }
    }

    private static string Domain(string address)
    {
        var at = address.LastIndexOf('@');
        return at >= 0 && at < address.Length - 1 ? address[(at + 1)..] : "";
    }

    private static IPAddress? RemoteIp(ISessionContext context) =>
        context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep)
            && ep is IPEndPoint ipep
            ? ipep.Address
            : null;
}
