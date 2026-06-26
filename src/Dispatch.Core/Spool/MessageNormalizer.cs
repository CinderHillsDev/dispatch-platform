using MimeKit;
using MimeKit.Utils;

namespace Dispatch.Core.Spool;

/// <summary>
/// Brings a relayed message up to the minimum RFC 5322 / deliverability bar before it leaves: a message MUST
/// carry a Date header (§3.6.1) and SHOULD carry a Message-Id (§3.6.4) — providers (Gmail, etc.) spam-filter
/// or reject mail missing them. We only add what the submitting client omitted; existing values are untouched.
/// </summary>
public static class MessageNormalizer
{
    /// <summary>Adds Date (set to <paramref name="receivedAt"/>) and/or a generated Message-Id if absent.
    /// Returns true if the message was changed.</summary>
    public static bool EnsureRequiredHeaders(MimeMessage message, DateTimeOffset receivedAt)
    {
        var changed = false;

        if (!message.Headers.Contains(HeaderId.Date))
        {
            message.Date = receivedAt;
            changed = true;
        }

        if (string.IsNullOrEmpty(message.MessageId))
        {
            message.MessageId = MimeUtils.GenerateMessageId();
            changed = true;
        }

        return changed;
    }
}
