using MimeKit;

namespace Dispatch.Core.Providers;

/// <summary>A parsed message handed to an <see cref="IRelayProvider"/> for upstream delivery.</summary>
public sealed class RelayMessage
{
    /// <summary>The fully parsed MIME message (parsed once by the worker, never on the receive path).</summary>
    public required MimeMessage Message { get; init; }

    public required string FromAddress { get; init; }
    public required IReadOnlyList<string> ToAddresses { get; init; }

    public string? SpoolId { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public long SizeBytes { get; init; }
}
