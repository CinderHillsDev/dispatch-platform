using Dispatch.Core.Providers;
using Dispatch.Providers;
using MimeKit;

namespace Dispatch.Providers.Tests;

// Direct unit tests for the shared provider helpers - SplitRecipients is the single point that keeps blind
// (Bcc) recipients out of the visible To/Cc across every structured provider, so its branches are tested here.
public class ProviderHttpTests
{
    private static RelayMessage Msg(MimeMessage mime, IReadOnlyList<string> envelope) =>
        new() { Message = mime, FromAddress = "s@x.com", ToAddresses = envelope };

    private static MimeMessage Mime(string[]? to = null, string[]? cc = null)
    {
        var m = new MimeMessage();
        m.From.Add(MailboxAddress.Parse("s@x.com"));
        foreach (var a in to ?? []) m.To.Add(MailboxAddress.Parse(a));
        foreach (var a in cc ?? []) m.Cc.Add(MailboxAddress.Parse(a));
        m.Subject = "s";
        m.Body = new TextPart("plain") { Text = "b" };
        return m;
    }

    [Fact]
    public void Splits_to_cc_and_bcc_from_headers_and_envelope()
    {
        // To+Cc in headers, plus one envelope-only recipient (the blind copy).
        var r = ProviderHttp.SplitRecipients(Msg(
            Mime(to: ["a@d.com"], cc: ["c@d.com"]),
            ["a@d.com", "c@d.com", "blind@d.com"]));

        Assert.Equal(["a@d.com"], r.To);
        Assert.Equal(["c@d.com"], r.Cc);
        Assert.Equal(["blind@d.com"], r.Bcc);   // envelope-only → bcc, never to/cc
    }

    [Fact]
    public void Header_recipient_not_in_envelope_is_excluded()
    {
        // A To header address that isn't an actual envelope recipient must not be delivered to.
        var r = ProviderHttp.SplitRecipients(Msg(
            Mime(to: ["a@d.com", "ghost@d.com"]),
            ["a@d.com"]));

        Assert.Equal(["a@d.com"], r.To);
        Assert.Empty(r.Cc);
        Assert.Empty(r.Bcc);
    }

    [Fact]
    public void Deduplicates_across_to_cc_and_envelope_case_insensitively()
    {
        var r = ProviderHttp.SplitRecipients(Msg(
            Mime(to: ["a@d.com"], cc: ["A@D.com"]),   // same address, different case, in both headers
            ["a@d.com"]));

        Assert.Equal(["a@d.com"], r.To);
        Assert.Empty(r.Cc);    // not re-listed in cc
        Assert.Empty(r.Bcc);
    }

    [Fact]
    public void No_visible_headers_promotes_envelope_to_to()
    {
        // Header-less / undisclosed-recipients message: everyone is in the envelope only.
        var r = ProviderHttp.SplitRecipients(Msg(Mime(), ["x@d.com", "y@d.com"]));

        Assert.Equal(["x@d.com", "y@d.com"], r.To);   // promoted to To, not left bcc-only
        Assert.Empty(r.Cc);
        Assert.Empty(r.Bcc);
    }

    [Fact]
    public void Attachments_decode_with_filename_and_content_type_fallbacks()
    {
        var m = Mime(to: ["a@d.com"]);
        var builder = new BodyBuilder { TextBody = "b" };
        builder.Attachments.Add("hello.txt", "file-data"u8.ToArray(), ContentType.Parse("text/plain"));
        m.Body = builder.ToMessageBody();

        var atts = ProviderHttp.Attachments(Msg(m, ["a@d.com"]));
        Assert.Single(atts);
        Assert.Equal("hello.txt", atts[0].FileName);
        Assert.Equal("text/plain", atts[0].ContentType);
        Assert.Equal("ZmlsZS1kYXRh", atts[0].Base64);   // base64("file-data")
        Assert.Equal("file-data"u8.ToArray(), atts[0].Content);
    }

    [Fact]
    public void Attachments_default_name_when_missing()
    {
        var m = Mime(to: ["a@d.com"]);
        var part = new MimePart("application", "octet-stream")
        {
            Content = new MimeContent(new MemoryStream("xyz"u8.ToArray())),
            ContentDisposition = new MimeKit.ContentDisposition(MimeKit.ContentDisposition.Attachment),
            // no FileName set
        };
        var multipart = new Multipart("mixed") { new TextPart("plain") { Text = "b" }, part };
        m.Body = multipart;

        var atts = ProviderHttp.Attachments(Msg(m, ["a@d.com"]));
        Assert.Single(atts);
        Assert.Equal("attachment", atts[0].FileName);                 // fallback name
        Assert.Equal("application/octet-stream", atts[0].ContentType);
    }
}
