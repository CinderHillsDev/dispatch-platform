using System.Collections.Concurrent;
using Dispatch.Core.Configuration;
using Dispatch.Core.Providers;
using Dispatch.Core.Relays;

namespace Dispatch.Data.Repositories;

/// <summary>
/// Stores a relay's provider + credentials in the SQL <c>config</c> table under <c>relay:{id}:…</c> keys,
/// encrypting secret fields transparently (spec §12.3, §19.5). Reads are cached for a short TTL so the
/// dispatch hot path doesn't query config per message.
/// </summary>
public sealed class SqlRelaySettingsStore(IConfigRepository config) : IRelaySettingsStore
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<int, (RelaySettings Value, DateTime At)> _cache = new();

    public async Task<RelaySettings> GetAsync(int relayId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(relayId, out var cached) && DateTime.UtcNow - cached.At < CacheTtl)
            return cached.Value;

        var providerText = await config.GetAsync($"relay:{relayId}:provider", ct);
        // Unset/unparseable → Unconfigured, so a never-configured relay refuses to relay (not silent discard).
        var provider = Enum.TryParse<RelayProviderType>(providerText, ignoreCase: true, out var p)
            ? p : RelayProviderType.Unconfigured;

        var settings = new Dictionary<string, string?>();
        foreach (var field in RelayProviderSchema.For(provider))
            settings[field.Name] = await config.GetAsync($"relay:{relayId}:{field.ConfigSuffix}", ct);

        var result = new RelaySettings(provider, settings);
        _cache[relayId] = (result, DateTime.UtcNow);
        return result;
    }

    public async Task SaveAsync(int relayId, RelaySettings settings, CancellationToken ct = default)
    {
        await config.SetAsync($"relay:{relayId}:provider", settings.Provider.ToString(), encrypted: false, ct);

        foreach (var field in RelayProviderSchema.For(settings.Provider))
        {
            if (!settings.Settings.TryGetValue(field.Name, out var value) || value is null)
                continue;   // null = leave existing value untouched (e.g. an unchanged secret)
            await config.SetAsync($"relay:{relayId}:{field.ConfigSuffix}", value, field.Secret, ct);
        }

        _cache.TryRemove(relayId, out _);
    }
}
