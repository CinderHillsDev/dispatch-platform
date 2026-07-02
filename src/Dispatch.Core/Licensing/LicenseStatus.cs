namespace Dispatch.Core.Licensing;

/// <summary>
/// Result of verifying a license key. A product is "Licensed" only when the signature is valid, the license
/// has not expired, and the key has not been revoked. The license key itself carries no organization name
/// (it's a compact, offline key); the issuer records who a key was issued to. The signature is bound to this
/// install's machine id, so a key issued for one machine will not verify on another. Mirrors the FluxDeploy
/// license format.
/// </summary>
public sealed record LicenseStatus(
    bool SignatureValid,
    bool Expired,
    bool Revoked,
    string? LicenseId,
    bool Perpetual,
    DateTime? ExpiresAt,
    string? Error)
{
    /// <summary>True when the key is authentic, in date, and not revoked - the product is fully licensed.</summary>
    public bool Licensed => SignatureValid && !Expired && !Revoked;

    public static LicenseStatus Invalid(string error) => new(false, false, false, null, false, null, error);
}
