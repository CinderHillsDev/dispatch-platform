namespace Dispatch.Core.Logging;

/// <summary>No-op log repository used until the SQL-backed relay_log repository lands (milestone: no SQL).</summary>
public sealed class NullLogRepository : ILogRepository
{
    public Task InsertAsync(RelayLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
}
