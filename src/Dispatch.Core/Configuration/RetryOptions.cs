namespace Dispatch.Core.Configuration;

/// <summary>Retry/back-off policy for transient relay failures (bound from "Retry").</summary>
public sealed class RetryOptions
{
    public const string SectionName = "Retry";

    /// <summary>Maximum retry attempts before a message is moved to spool/failed/.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Back-off delay (seconds) before each retry; the last value repeats for further attempts (§6.7).
    /// Empty falls back to <see cref="DefaultDelaysSeconds"/>. Left empty by default so configuration
    /// values replace rather than append (array-binding quirk).</summary>
    public double[] DelaysSeconds { get; set; } = [];

    public static readonly double[] DefaultDelaysSeconds = [30, 300, 1800];

    public double[] EffectiveDelaysSeconds => DelaysSeconds is { Length: > 0 } ? DelaysSeconds : DefaultDelaysSeconds;
}
