using System.Buffers;
using System.Net;
using Microsoft.Extensions.Logging;
using MimeKit;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Net;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace Dispatch.Core.Spool;

/// <summary>
/// The ONLY work performed between SMTP DATA completion and <c>250 OK</c> (spec §6.4, §19.3):
/// write raw bytes to spool/incoming/, write a small .meta sidecar, ring the doorbell.
/// No database, no network — the only failure mode that blocks 250 OK is a full disk.
/// </summary>
public sealed class SpoolMessageStore : MessageStore
{
    private readonly SpoolDirectory _spool;
    private readonly ILogger<SpoolMessageStore> _log;

    public SpoolMessageStore(SpoolDirectory spool, ILogger<SpoolMessageStore> log)
    {
        _spool = spool;
        _log = log;
    }

    public override async Task<SmtpResponse> SaveAsync(
        ISessionContext context,
        IMessageTransaction transaction,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var path = _spool.IncomingPath(id);

        // Disk write — the hot path. Stream the message straight to the spool file segment-by-segment so we
        // never hold the whole body as an extra in-memory byte[] (bounded only by the SMTP SIZE ceiling).
        long size;
        await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            foreach (var segment in buffer)
                await fs.WriteAsync(segment, cancellationToken);
            size = fs.Length;
        }

        var from = SafeAddress(transaction.From);
        var to = transaction.To?
            .Select(SafeAddress)
            .Where(a => !string.IsNullOrEmpty(a))
            .ToArray() ?? [];

        // Minimal header scan for X-Dispatch-Tag and a From fallback — not a full MIME parse.
        string[]? tags = null;
        try
        {
            await using var rs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            var msg = await MimeMessage.LoadAsync(rs, cancellationToken);
            tags = msg.Headers
                .Where(h => h.Field.Equals("X-Dispatch-Tag", StringComparison.OrdinalIgnoreCase))
                .Select(h => h.Value.Trim())
                .Where(v => v.Length > 0)
                .ToArray();
            if (tags.Length == 0) tags = null;

            if (string.IsNullOrEmpty(from) && msg.From.Mailboxes.FirstOrDefault() is { } mb)
                from = mb.Address;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Header scan failed for {SpoolId}; storing without tags", id);
        }

        var meta = new SpoolMeta
        {
            SpoolId = id,
            ReceivedAt = DateTime.UtcNow,
            FromAddress = from,
            ToAddresses = to,
            IngestSource = "SMTP",
            SourceIp = RemoteIp(context),
            Tags = tags,
        };
        meta.Save(path);

        _spool.Signal(Path.GetFileName(path));
        _log.LogInformation(
            "Received {SpoolId} from {From} ({Size} bytes) → 250 OK", id, from, size);

        return SmtpResponse.Ok;
    }

    private static string SafeAddress(IMailbox? mailbox)
    {
        if (mailbox is null || ReferenceEquals(mailbox, Mailbox.Empty)) return "";
        var addr = mailbox.AsAddress();
        return addr == "@" ? "" : addr;
    }

    private static string? RemoteIp(ISessionContext context) =>
        context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep)
            && ep is IPEndPoint ipep
            ? ipep.Address.ToString()
            : null;
}
