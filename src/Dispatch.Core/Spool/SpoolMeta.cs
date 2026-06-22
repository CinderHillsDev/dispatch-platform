using System.Text.Json;

namespace Dispatch.Core.Spool;

/// <summary>
/// JSON sidecar (<c>{uuid}.meta</c>) holding envelope + retry state (spec §6.3).
/// The .eml is immutable; only the .meta changes across retries.
/// </summary>
public sealed class SpoolMeta
{
    public Guid SpoolId { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string FromAddress { get; set; } = "";
    public string[] ToAddresses { get; set; } = [];
    public string IngestSource { get; set; } = "SMTP";   // SMTP | API
    public string? SourceIp { get; set; }
    public int? ApiKeyId { get; set; }
    public string? ApiKeyName { get; set; }
    public string[]? Tags { get; set; }
    /// <summary>Originating client from the X-Mailer header, if present (shown in the Message Log detail).</summary>
    public string? XMailer { get; set; }
    /// <summary>Number of attachments in the message (shown in the Message Log detail).</summary>
    public int AttachmentCount { get; set; }
    public int RetryCount { get; set; }
    /// <summary>True once this message has been counted as Received, so crash-recovery can't double-count it.</summary>
    public bool ReceivedCounted { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public int? LastRelayId { get; set; }
    public string? LastError { get; set; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The .meta path for a given .eml path (same name, .meta extension).</summary>
    public static string PathFor(string emlPath) => Path.ChangeExtension(emlPath, ".meta");

    public void Save(string emlPath) =>
        File.WriteAllText(PathFor(emlPath), JsonSerializer.Serialize(this, Json));

    /// <summary>Full load — used by the worker after claiming a file.</summary>
    public static SpoolMeta Load(string emlPath) =>
        JsonSerializer.Deserialize<SpoolMeta>(File.ReadAllText(PathFor(emlPath)), Json)!;

    /// <summary>
    /// Reads the .meta without requiring the .eml — used by the relay-aware claim loop.
    /// Returns null if the meta is missing (file just written) or corrupt (being written).
    /// </summary>
    public static SpoolMeta? Peek(string emlPath)
    {
        var metaPath = PathFor(emlPath);
        if (!File.Exists(metaPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<SpoolMeta>(File.ReadAllText(metaPath), Json);
        }
        catch
        {
            return null;
        }
    }
}
