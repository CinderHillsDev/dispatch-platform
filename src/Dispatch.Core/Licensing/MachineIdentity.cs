using Dispatch.Core.Configuration;

namespace Dispatch.Core.Licensing;

/// <summary>
/// This install's stable <b>Machine ID</b> - a random GUID minted on first run and persisted in the SQL config
/// table (<see cref="ConfigKeys.LicenseMachineId"/>). License keys are node-locked to it: the issuer signs
/// <c>payload || machineId</c>, so a key only verifies on the install it was issued for. A reinstall gets a
/// fresh GUID (and therefore needs a reissued key), which is what stops a leaked key from being reused.
///
/// The value is generated once and never changes for the life of the install; it survives restarts, upgrades,
/// and IP/hostname changes. It is surfaced (read-only) on the dashboard so the customer can send it at purchase.
/// </summary>
public sealed class MachineIdentity(IConfigRepository config)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile string? _cached;

    /// <summary>Returns the Machine ID, minting and persisting it on first call if the install has none.</summary>
    public async ValueTask<string> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cached is not null) return _cached;

            var existing = await config.GetAsync(ConfigKeys.LicenseMachineId, ct);
            if (!string.IsNullOrWhiteSpace(existing))
                return _cached = LicenseVerifier.NormalizeMachineId(existing);

            // First run: mint a stable id (lowercase GUID; NormalizeMachineId is a no-op on it) and persist.
            var id = Guid.NewGuid().ToString();
            await config.SetAsync(ConfigKeys.LicenseMachineId, id, ct: ct);
            return _cached = id;
        }
        finally
        {
            _lock.Release();
        }
    }
}
