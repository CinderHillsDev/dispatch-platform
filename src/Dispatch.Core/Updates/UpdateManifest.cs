using System.Text.Json.Serialization;

namespace Dispatch.Core.Updates;

/// <summary>One platform payload inside an upgrade package: its file name within the package and the
/// SHA-256 of that file.</summary>
public sealed record UpdateArtifact(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>
/// Describes a single, cross-platform upgrade package (spec: web-UI updates). ONE package per release
/// carries every platform's self-contained payload under <see cref="Artifacts"/> (keyed by runtime id,
/// e.g. <c>linux-x64</c>, <c>linux-arm64</c>, <c>win-x64</c>); the box applies whichever matches its arch.
/// Emitted by release.yml and signed (detached RSA/SHA-256) so a host can authenticate it before applying.
/// </summary>
public sealed record UpdateManifest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("minFromVersion")] string MinFromVersion,
    [property: JsonPropertyName("builtAt")] string BuiltAt,
    [property: JsonPropertyName("notesUrl")] string? NotesUrl,
    [property: JsonPropertyName("artifacts")] Dictionary<string, UpdateArtifact> Artifacts);
