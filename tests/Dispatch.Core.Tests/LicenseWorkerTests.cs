using System.Security.Cryptography;
using Dispatch.Core.Configuration;
using Dispatch.Core.Licensing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dispatch.Core.Tests;

public class LicenseWorkerTests
{
    private sealed class FakeConfigRepo : IConfigRepository
    {
        public readonly Dictionary<string, string> Store = new(StringComparer.OrdinalIgnoreCase);
        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Store.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value, bool encrypted = false, CancellationToken ct = default)
        { Store[key] = value; return Task.CompletedTask; }
        public Task<IReadOnlyList<ConfigEntry>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConfigEntry>>(Store.Select(kv => new ConfigEntry(kv.Key, kv.Value, false, default)).ToList());
    }

    private static string MakeKey(ECDsa signer, string machineId, ushort seq = 7, byte expiryMonth = 0)
    {
        var payload = new byte[6];
        payload[0] = (byte)(seq & 0xFF);
        payload[1] = (byte)(seq >> 8);
        payload[2] = expiryMonth;
        var signed = LicenseVerifier.SignedMessage(payload, machineId);
        var sig = signer.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var raw = new byte[payload.Length + sig.Length];
        Buffer.BlockCopy(payload, 0, raw, 0, payload.Length);
        Buffer.BlockCopy(sig, 0, raw, payload.Length, sig.Length);
        return LicenseVerifier.FormatKey(LicenseVerifier.Base32Encode(raw));
    }

    private static (LicenseWorker worker, LicenseGate gate, FakeConfigRepo repo, ECDsa signer, LicenseService svc) New()
    {
        var repo = new FakeConfigRepo();
        var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var svc = new LicenseService(repo, new MachineIdentity(repo), new LicenseVerifier(ec.ExportSubjectPublicKeyInfoPem()));
        var gate = new LicenseGate();
        var worker = new LicenseWorker(svc, gate, NullLogger<LicenseWorker>.Instance);
        return (worker, gate, repo, ec, svc);
    }

    [Fact]
    public async Task Gate_stays_open_during_grace()
    {
        var (worker, gate, _, _, _) = New();
        await worker.RefreshAsync();
        Assert.False(gate.EnforcementActive);
    }

    [Fact]
    public async Task Gate_closes_when_unlicensed_past_grace()
    {
        var (worker, gate, repo, _, _) = New();
        repo.Store[ConfigKeys.LicenseFirstRunUtc] = DateTime.UtcNow.AddDays(-31).ToString("O");

        await worker.RefreshAsync();

        Assert.True(gate.EnforcementActive);
    }

    [Fact]
    public async Task Gate_reopens_after_a_valid_key_is_saved()
    {
        var (worker, gate, repo, signer, svc) = New();
        repo.Store[ConfigKeys.LicenseFirstRunUtc] = DateTime.UtcNow.AddDays(-31).ToString("O");
        await worker.RefreshAsync();
        Assert.True(gate.EnforcementActive);   // enforcing before a key

        var machineId = await svc.GetMachineIdAsync();
        await svc.SaveKeyAsync(MakeKey(signer, machineId));
        await worker.RefreshAsync();

        Assert.False(gate.EnforcementActive);  // key clears enforcement
    }
}
