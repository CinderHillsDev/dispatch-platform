using Dispatch.Core.Providers;

namespace Dispatch.Core.Relays;

/// <summary>A row from the SQL <c>relays</c> table (spec §6.11).</summary>
public sealed class RelayRecord
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public RelayProviderType Provider { get; init; }
    public bool IsDefault { get; init; }
    public bool Enabled { get; init; } = true;
    public int MaxConcurrency { get; init; }
    public long MaxMessageBytes { get; init; }
}
