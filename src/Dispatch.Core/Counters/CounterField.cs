namespace Dispatch.Core.Counters;

/// <summary>Daily aggregate counter fields (spec §6.11 relay_counters).</summary>
public enum CounterField
{
    Received,
    Delivered,
    Failed,
    Retried,
    Denied,
}
