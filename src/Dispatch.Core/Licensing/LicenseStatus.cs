namespace Dispatch.Core.Licensing;

/// <summary>
/// Result of verifying a license key. A product is "Licensed" only when the signature is valid AND the
/// license has not expired. The license key itself carries no organization name (it's a compact, offline
/// key); the issuer records who a key was issued to. Mirrors the FluxDeploy license format.
/// </summary>
public sealed record LicenseStatus(
    bool SignatureValid,
    bool Expired,
    string? LicenseId,
    bool Perpetual,
    DateTime? ExpiresAt,
    string? Error)
{
    /// <summary>True when the key is authentic and in date - the product is fully licensed.</summary>
    public bool Licensed => SignatureValid && !Expired;

    public static LicenseStatus Invalid(string error) => new(false, false, null, false, null, error);
}
