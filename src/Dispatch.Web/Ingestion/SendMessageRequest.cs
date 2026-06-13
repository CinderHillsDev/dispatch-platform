namespace Dispatch.Web.Ingestion;

/// <summary>JSON body shape for <c>POST /api/v1/messages</c> (spec §7.4).</summary>
public sealed class SendMessageRequest
{
    public string? From { get; set; }
    public string[]? To { get; set; }
    public string[]? Cc { get; set; }
    public string[]? Bcc { get; set; }
    public string? Subject { get; set; }
    public string? Text { get; set; }
    public string? Html { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public string[]? Tags { get; set; }
}
