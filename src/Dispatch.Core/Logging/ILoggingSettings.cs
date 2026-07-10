namespace Dispatch.Core.Logging;

/// <summary>
/// Live relay_log suppression toggles (spec §6.6, §12.3 logging.*). Counters are always written; these
/// only control whether the corresponding relay_log rows are inserted. Backed by the SQL config table
/// with a short cache so the worker doesn't query per message.
/// </summary>
public interface ILoggingSettings
{
    ValueTask<bool> LogDeliveredAsync(CancellationToken ct = default);
    ValueTask<bool> LogRetryingAsync(CancellationToken ct = default);
    ValueTask<bool> LogDeniedAsync(CancellationToken ct = default);
}

/// <summary>Default that logs everything - used in tests and when no config store is wired.</summary>
public sealed class AlwaysLogSettings : ILoggingSettings
{
    public ValueTask<bool> LogDeliveredAsync(CancellationToken ct = default) => ValueTask.FromResult(true);
    public ValueTask<bool> LogRetryingAsync(CancellationToken ct = default) => ValueTask.FromResult(true);
    public ValueTask<bool> LogDeniedAsync(CancellationToken ct = default) => ValueTask.FromResult(true);
}
