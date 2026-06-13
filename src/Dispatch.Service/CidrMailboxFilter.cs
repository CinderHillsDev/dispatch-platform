using Dispatch.Core.Configuration;
using Dispatch.Core.Counters;
using Dispatch.Core.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Net;
using SmtpServer.Storage;
using System.Net;

namespace Dispatch.Service;

/// <summary>
/// Application-layer access control (spec §5.3, §14.2): refuses MAIL FROM when the source IP is outside
/// the configured allow-list, enforces the global max message size at MAIL FROM, and enforces the
/// per-relay size limit at RCPT TO (before DATA) by running the routing engine. Denied attempts are counted.
/// </summary>
public sealed class CidrMailboxFilter : IMailboxFilter
{
    /// <summary>Session property key holding the SIZE= declared in MAIL FROM (int; 0 if not declared).</summary>
    internal const string DeclaredSizeKey = "Dispatch.DeclaredSize";

    private readonly IPNetwork[] _allowed;
    private readonly long _maxBytes;
    private readonly ICounterRepository _counters;
    private readonly IRelayResolver _routing;
    private readonly ILogger<CidrMailboxFilter> _log;

    public CidrMailboxFilter(
        IOptions<ListenerOptions> options,
        ICounterRepository counters,
        IRelayResolver routing,
        ILogger<CidrMailboxFilter> log)
    {
        var o = options.Value;
        _allowed = o.EffectiveAllowedCidrs
            .Select(c => IPNetwork.TryParse(c, out var n) ? (IPNetwork?)n : null)
            .Where(n => n.HasValue)
            .Select(n => n!.Value)
            .ToArray();
        _maxBytes = o.MaxMessageBytes;
        _counters = counters;
        _routing = routing;
        _log = log;
    }

    public Task<bool> CanAcceptFromAsync(
        ISessionContext context, IMailbox from, int size, CancellationToken cancellationToken)
    {
        // Capture the declared SIZE= for the per-relay check at RCPT TO (spec §14.2).
        context.Properties[DeclaredSizeKey] = size;

        if (_maxBytes > 0 && size > _maxBytes)
        {
            _log.LogWarning("Rejecting MAIL FROM {From}: size {Size} exceeds limit {Limit}",
                from.AsAddress(), size, _maxBytes);
            return Task.FromResult(false);
        }

        var ip = RemoteIp(context);
        if (ip is null || _allowed.Length == 0)
            return Task.FromResult(true);   // no endpoint info or no allow-list configured → allow

        var test = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
        var allowed = _allowed.Any(n => n.Contains(test) || n.Contains(ip));

        if (!allowed)
        {
            _ = _counters.IncrementAsync(0, CounterField.Denied, cancellationToken);
            _log.LogWarning("Denied connection from {Ip} (not in allow-list)", ip);
        }

        return Task.FromResult(allowed);
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
            _ = _counters.IncrementAsync(0, CounterField.Denied, cancellationToken);
            _log.LogWarning(
                "Rejecting RCPT TO {To}: declared size {Size} exceeds relay \"{Relay}\" limit {Limit}",
                to.AsAddress(), declaredSize, relay.Name, limit);
            return false;
        }

        return true;
    }

    private static IPAddress? RemoteIp(ISessionContext context) =>
        context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep)
            && ep is IPEndPoint ipep
            ? ipep.Address
            : null;
}
