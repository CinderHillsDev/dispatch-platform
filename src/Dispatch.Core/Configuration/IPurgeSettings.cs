namespace Dispatch.Core.Configuration;

/// <summary>
/// Live retention + auto-purge thresholds (spec §6.10, §12.3 purge.*). Backed by the SQL config table
/// with a short cache; falls back to the appsettings <see cref="PurgeOptions"/> defaults when a key is
/// unset. The purge worker resolves this once per cycle (off the hot path), so the cache only avoids
/// redundant SQL round-trips when the size-pressure loop re-evaluates frequently.
/// </summary>
public interface IPurgeSettings
{
    ValueTask<PurgeOptions> GetAsync(CancellationToken ct = default);
}

/// <summary>Default that returns the static <see cref="PurgeOptions"/> - used in tests and when no config store is wired.</summary>
public sealed class OptionsPurgeSettings(PurgeOptions options) : IPurgeSettings
{
    public ValueTask<PurgeOptions> GetAsync(CancellationToken ct = default) => ValueTask.FromResult(options);
}
