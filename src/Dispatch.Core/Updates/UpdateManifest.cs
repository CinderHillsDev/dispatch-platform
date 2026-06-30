using System.Text.Json.Serialization;

namespace Dispatch.Core.Updates;

/// <summary>
/// Describes a published upgrade bundle (spec: web-UI updates). Emitted by release.yml alongside the
/// <c>dispatch-upgrade-&lt;ver&gt;-linux-&lt;arch&gt;.tar.gz</c> payload and signed (detached RSA/SHA-256)
/// so an appliance can authenticate it before applying. <see cref="Sha256"/> is the hex digest of the
/// payload tarball - the signature covers this manifest, and the manifest vouches for the payload.
/// </summary>
public sealed record UpdateManifest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("arch")] string Arch,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("minFromVersion")] string MinFromVersion,
    [property: JsonPropertyName("builtAt")] string BuiltAt,
    [property: JsonPropertyName("notesUrl")] string? NotesUrl);
