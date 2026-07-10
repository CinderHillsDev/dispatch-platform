using System.Text;
using Dispatch.Core.Spool;
using MimeKit;

namespace Dispatch.Core.Tests;

public class MessageNormalizerTests
{
    private static readonly DateTimeOffset Received = new(2026, 6, 26, 12, 0, 0, TimeSpan.Zero);

    private static MimeMessage Load(string raw) => MimeMessage.Load(new MemoryStream(Encoding.ASCII.GetBytes(raw)));

    [Fact]
    public void Adds_date_and_message_id_when_missing()
    {
        var msg = Load("From: a@b.com\r\nTo: c@d.com\r\nSubject: s\r\n\r\nbody\r\n");
        Assert.False(msg.Headers.Contains(HeaderId.Date));
        Assert.True(string.IsNullOrEmpty(msg.MessageId));

        var changed = MessageNormalizer.EnsureRequiredHeaders(msg, Received);

        Assert.True(changed);
        Assert.Equal(Received, msg.Date);
        Assert.False(string.IsNullOrEmpty(msg.MessageId));
        Assert.Contains("@", msg.MessageId);   // well-formed "id@domain"
    }

    [Fact]
    public void Preserves_existing_date_and_message_id()
    {
        var msg = Load(
            "From: a@b.com\r\nTo: c@d.com\r\nDate: Wed, 01 Jan 2025 00:00:00 +0000\r\n" +
            "Message-Id: <original@sender>\r\nSubject: s\r\n\r\nbody\r\n");

        var changed = MessageNormalizer.EnsureRequiredHeaders(msg, Received);

        Assert.False(changed);
        Assert.Equal("original@sender", msg.MessageId);
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), msg.Date);
    }

    [Fact]
    public void Adds_only_the_missing_one()
    {
        var msg = Load("From: a@b.com\r\nTo: c@d.com\r\nDate: Wed, 01 Jan 2025 00:00:00 +0000\r\nSubject: s\r\n\r\nbody\r\n");

        var changed = MessageNormalizer.EnsureRequiredHeaders(msg, Received);

        Assert.True(changed);
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), msg.Date);   // existing Date kept
        Assert.False(string.IsNullOrEmpty(msg.MessageId));                                 // Message-Id added
    }
}
