using System.Text.Json.Serialization;

namespace Dispatch.Core.Updates;

/// <summary>Progress of an in-place update. The platform updater (Linux systemd/bash or the Windows
/// helper) writes this to <c>updates/status.json</c>; the dashboard reads it to show progress.</summary>
public enum UpdateState { Idle, Staged, Applying, Succeeded, Failed, RolledBack }

/// <summary>Status surfaced to the dashboard. State is serialized as a string for the bash/JSON interop.</summary>
public sealed record UpdateStatus(
    [property: JsonPropertyName("state")] UpdateState State,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("updatedAtUtc")] DateTime UpdatedAtUtc);

/// <summary>The hand-off record the web service writes to <c>updates/apply.request</c> once a bundle is
/// verified and staged; the platform updater picks it up, applies the staged bundle, restarts, and writes
/// back <see cref="UpdateStatus"/>.</summary>
public sealed record ApplyRequest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("arch")] string Arch,
    [property: JsonPropertyName("stagedDir")] string StagedDir,
    [property: JsonPropertyName("fromVersion")] string FromVersion,
    [property: JsonPropertyName("requestedAtUtc")] DateTime RequestedAtUtc);
