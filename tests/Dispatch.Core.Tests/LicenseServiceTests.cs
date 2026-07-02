using System.Security.Cryptography;
using Dispatch.Core.Configuration;
using Dispatch.Core.Licensing;

namespace Dispatch.Core.Tests;

public class LicenseServiceTests
{
    // Dictionary-backed config repo (no SQL).
    private sealed class FakeConfigRepo : IConfigRepository
    {
        public readonly Dictionary<string, string> Store = new(StringComparer.OrdinalIgnoreCase);

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Store.TryGetValue(key, out var v) ? v : null);

        public Task SetAsync(string key, string value, bool encrypted = false, CancellationToken ct = default)
        {
            Store[key] = value;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConfigEntry>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConfigEntry>>(
                Store.Select(kv => new ConfigEntry(kv.Key, kv.Value, false, default)).ToList());
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

    private static (LicenseService svc, FakeConfigRepo repo, ECDsa signer) NewService()
    {
        var repo = new FakeConfigRepo();
        var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = new LicenseVerifier(ec.ExportSubjectPublicKeyInfoPem());
        var svc = new LicenseService(repo, new MachineIdentity(repo), verifier);
        return (svc, repo, ec);
    }

    [Fact]
    public async Task MachineIdentity_mints_once_and_is_stable()
    {
        var repo = new FakeConfigRepo();
        var m = new MachineIdentity(repo);

        var a = await m.GetAsync();
        var b = await m.GetAsync();

        Assert.False(string.IsNullOrWhiteSpace(a));
        Assert.Equal(a, b);
        Assert.Equal(a, repo.Store[ConfigKeys.LicenseMachineId]);
        Assert.True(Guid.TryParse(a, out _));
    }

    [Fact]
    public async Task MachineIdentity_reuses_existing_id()
    {
        var repo = new FakeConfigRepo();
        repo.Store[ConfigKeys.LicenseMachineId] = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE";

        var id = await new MachineIdentity(repo).GetAsync();

        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", id);  // normalized to lowercase
    }

    [Fact]
    public async Task No_key_is_operational_during_grace()
    {
        var (svc, _, _) = NewService();

        var snap = await svc.EvaluateAsync();

        Assert.False(snap.HasKey);
        Assert.True(snap.InGracePeriod);
        Assert.True(snap.Operational);
        Assert.False(snap.EnforcementActive);
        Assert.InRange(snap.GraceDaysRemaining, 29, 30);
    }

    [Fact]
    public async Task No_key_past_grace_enforces()
    {
        var (svc, repo, _) = NewService();
        repo.Store[ConfigKeys.LicenseFirstRunUtc] = DateTime.UtcNow.AddDays(-31).ToString("O");

        var snap = await svc.EvaluateAsync();

        Assert.False(snap.InGracePeriod);
        Assert.False(snap.Operational);
        Assert.True(snap.EnforcementActive);
        Assert.Equal(0, snap.GraceDaysRemaining);
    }

    [Fact]
    public async Task Valid_key_is_licensed_and_not_in_grace()
    {
        var (svc, repo, signer) = NewService();
        var machineId = await svc.GetMachineIdAsync();
        repo.Store[ConfigKeys.LicenseFirstRunUtc] = DateTime.UtcNow.AddDays(-100).ToString("O"); // grace long gone

        repo.Store[ConfigKeys.LicenseKey] = MakeKey(signer, machineId);
        var snap = await svc.EvaluateAsync();

        Assert.True(snap.Status.Licensed);
        Assert.True(snap.Operational);
        Assert.False(snap.InGracePeriod);
        Assert.False(snap.EnforcementActive);
    }

    [Fact]
    public async Task SaveKey_rejects_a_key_for_another_machine_and_accepts_a_valid_one()
    {
        var (svc, repo, signer) = NewService();
        var machineId = await svc.GetMachineIdAsync();

        var wrong = await svc.SaveKeyAsync(MakeKey(signer, "some-other-machine-id"));
        Assert.False(wrong.Ok);
        Assert.False(repo.Store.ContainsKey(ConfigKeys.LicenseKey));

        var right = await svc.SaveKeyAsync(MakeKey(signer, machineId));
        Assert.True(right.Ok);
        Assert.True(right.Status.Licensed);
        Assert.True(repo.Store.ContainsKey(ConfigKeys.LicenseKey));
    }
}
