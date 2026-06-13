namespace Dispatch.Core.Logging;

/// <summary>
/// Writes relay_log rows (spec §6.11, §19.3). The ONLY SQL touch point in the worker.
/// Implementations MUST swallow failures — mail delivery must never fail because the log DB is down.
/// </summary>
public interface ILogRepository
{
    Task InsertAsync(RelayLogEntry entry, CancellationToken ct = default);
}
