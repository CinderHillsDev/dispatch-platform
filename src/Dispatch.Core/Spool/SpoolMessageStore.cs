using System.Buffers;
using System.Net;
using System.Text;
using Dispatch.Core.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;
using MimeKit.Utils;
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
    // RFC 5321 §6.3 loop defence: refuse a message that already carries an absurd number of Received
    // headers (each hop adds one), so a misconfigured relay loop is broken instead of amplified.
    private const int MaxReceivedHeaders = 30;

    private readonly SpoolDirectory _spool;
    private readonly ConfigCache _config;
    private readonly ILogger<SpoolMessageStore> _log;

    public SpoolMessageStore(SpoolDirectory spool, ConfigCache config, ILogger<SpoolMessageStore> log)
    {
        _spool = spool;
        _config = config;
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

        var from = SafeAddress(transaction.From);
        var to = transaction.To?
            .Select(SafeAddress)
            .Where(a => !string.IsNullOrEmpty(a))
            .ToArray() ?? [];

        // Prepend a Received: trace header (RFC 5321 §4.4) so the relay hop is recorded for debugging and
        // loop detection, and so downstream raw-MIME providers forward a conformant message.
        var receivedHeader = BuildReceivedHeader(context, id, to.FirstOrDefault());

        // Disk write — the hot path. Stream the message straight to the spool file segment-by-segment so we
        // never hold the whole body as an extra in-memory byte[] (bounded only by the SMTP SIZE ceiling).
        long size;
        await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            await fs.WriteAsync(receivedHeader, cancellationToken);
            foreach (var segment in buffer)
                await fs.WriteAsync(segment, cancellationToken);
            size = fs.Length;
        }

        // Minimal header scan for X-Dispatch-Tag and a From fallback — not a full MIME parse.
        string[]? tags = null;
        string? xMailer = null;
        var attachmentCount = 0;
        var receivedCount = 0;
        try
        {
            await using var rs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            var msg = await MimeMessage.LoadAsync(rs, cancellationToken);
            receivedCount = msg.Headers.Count(h => h.Field.Equals("Received", StringComparison.OrdinalIgnoreCase));
            tags = msg.Headers
                .Where(h => h.Field.Equals("X-Dispatch-Tag", StringComparison.OrdinalIgnoreCase))
                .Select(h => h.Value.Trim())
                .Where(v => v.Length > 0)
                .ToArray();
            if (tags.Length == 0) tags = null;

            xMailer = msg.Headers["X-Mailer"]?.Trim();
            if (string.IsNullOrEmpty(xMailer)) xMailer = null;
            attachmentCount = msg.Attachments.Count();

            if (string.IsNullOrEmpty(from) && msg.From.Mailboxes.FirstOrDefault() is { } mb)
                from = mb.Address;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Header scan failed for {SpoolId}; storing without tags", id);
        }

        // Mail-loop defence (RFC 5321 §6.3): too many Received headers means the message is bouncing between
        // relays — drop it permanently rather than spool and amplify the loop.
        if (receivedCount > MaxReceivedHeaders)
        {
            _log.LogWarning("Rejecting {SpoolId} from {From}: {Count} Received headers — mail loop", id, from, receivedCount);
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
            return new SmtpResponse(SmtpReplyCode.TransactionFailed, "Too many Received headers — mail loop detected");
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
            XMailer = xMailer,
            AttachmentCount = attachmentCount,
        };
        meta.Save(path);

        _spool.Signal(Path.GetFileName(path));
        _log.LogInformation(
            "Received {SpoolId} from {From} ({Size} bytes) → 250 OK", id, from, size);

        return SmtpResponse.Ok;
    }

    /// <summary>
    /// Builds the RFC 5321 §4.4 <c>Received:</c> trace header for this hop: where it came from (HELO + source
    /// IP), this server, the transport (ESMTP/ESMTPS), a queue id, the recipient, and the timestamp.
    /// </summary>
    private byte[] BuildReceivedHeader(ISessionContext context, Guid id, string? firstRecipient)
    {
        var ip = RemoteIp(context) ?? "unknown";
        var server = _config.Listener().ServerName;
        if (string.IsNullOrWhiteSpace(server)) server = Environment.MachineName;
        var secure = false;
        try { secure = context.Pipe?.IsSecure ?? false; } catch { /* pipe state best-effort */ }
        var proto = secure ? "ESMTPS" : "ESMTP";
        var queueId = id.ToString("N")[..16];
        var date = DateUtils.FormatDate(DateTimeOffset.UtcNow);

        var sb = new StringBuilder();
        sb.Append("Received: from [").Append(ip).Append("]\r\n");
        sb.Append("\tby ").Append(server).Append(" (Dispatch SMTP Relay) with ").Append(proto)
          .Append(" id ").Append(queueId).Append("\r\n");
        if (!string.IsNullOrEmpty(firstRecipient))
            sb.Append("\tfor <").Append(firstRecipient).Append(">; ").Append(date).Append("\r\n");
        else
            sb.Append("\t; ").Append(date).Append("\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
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
