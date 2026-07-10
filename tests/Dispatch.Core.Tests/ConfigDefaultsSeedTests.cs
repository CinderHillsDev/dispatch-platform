using Dispatch.Core.Configuration;

namespace Dispatch.Core.Tests;

// Verifies the seeding *behaviour* (idempotent, never overwrites operator edits), complementing
// ConfigDefaultsTests which asserts the default *values*.
public class ConfigDefaultsSeedTests
{
    // Minimal dictionary-backed config repository (no SQL) that counts writes.
    private sealed class FakeConfigRepo : IConfigRepository
    {
        public readonly Dictionary<string, string> Store = new(StringComparer.OrdinalIgnoreCase);
        public int Writes;

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Store.TryGetValue(key, out var v) ? v : null);

        public Task SetAsync(string key, string value, bool encrypted = false, CancellationToken ct = default)
        {
            Store[key] = value;
            Writes++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConfigEntry>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConfigEntry>>(
                Store.Select(kv => new ConfigEntry(kv.Key, kv.Value, false, default)).ToList());
    }

    [Fact]
    public async Task Seeds_all_missing_defaults_into_empty_repo()
    {
        var repo = new FakeConfigRepo();
        await ConfigDefaults.SeedAsync(repo);

        Assert.Equal(ConfigDefaults.Defaults.Count, repo.Store.Count);
        foreach (var (key, value) in ConfigDefaults.Defaults)
            Assert.Equal(value, repo.Store[key]);
    }

    [Fact]
    public async Task Does_not_overwrite_an_operator_edited_value()
    {
        var repo = new FakeConfigRepo();
        repo.Store[ConfigKeys.ListenerServerName] = "MyCustomName";   // operator edit
        repo.Writes = 0;

        await ConfigDefaults.SeedAsync(repo);

        Assert.Equal("MyCustomName", repo.Store[ConfigKeys.ListenerServerName]);   // untouched
        Assert.True(repo.Store.ContainsKey(ConfigKeys.ListenerPorts));             // others still seeded
    }

    [Fact]
    public async Task Is_idempotent_second_seed_writes_nothing()
    {
        var repo = new FakeConfigRepo();
        await ConfigDefaults.SeedAsync(repo);
        repo.Writes = 0;

        await ConfigDefaults.SeedAsync(repo);   // second pass

        Assert.Equal(0, repo.Writes);
    }
}
