using Dispatch.Core.Configuration;
using Dispatch.Core.Counters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Net;
using SmtpServer.Storage;
using System.Net;

namespace Dispatch.Service;

/// <summary>
/// Application-layer access control (spec §5.3): refuses MAIL FROM when the source IP is outside
/// the configured allow-list, and enforces the global max message size. Denied attempts are counted.
/// </summary>
public sealed class CidrMailboxFilter : IMailboxFilter
{
    private readonly IPNetwork[] _allowed;
    private readonly long _maxBytes;
    private readonly ICounterRepository _counters;
    private readonly ILogger<CidrMailboxFilter> _log;

    public CidrMailboxFilter(
        IOptions<ListenerOptions> options,
        ICounterRepository counters,
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
        _log = log;
    }

    public Task<bool> CanAcceptFromAsync(
        ISessionContext context, IMailbox from, int size, CancellationToken cancellationToken)
    {
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

    public Task<bool> CanDeliverToAsync(
        ISessionContext context, IMailbox to, IMailbox from, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    private static IPAddress? RemoteIp(ISessionContext context) =>
        context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep)
            && ep is IPEndPoint ipep
            ? ipep.Address
            : null;
}
