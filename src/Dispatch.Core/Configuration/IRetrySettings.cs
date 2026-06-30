namespace Dispatch.Core.Configuration;

/// <summary>
/// Live retry/back-off policy (spec §6.7, §12.3 spool.max_retries / spool.retry_delays_seconds).
/// Backed by the SQL config table with a short cache so the worker doesn't query per message; falls
/// back to the appsettings <see cref="RetryOptions"/> defaults when a key is unset.
/// </summary>
public interface IRetrySettings
{
    ValueTask<RetryPolicy> GetAsync(CancellationToken ct = default);
}

/// <summary>Resolved retry policy snapshot.</summary>
public sealed record RetryPolicy(int MaxRetries, double[] DelaysSeconds)
{
    /// <summary>Back-off before <paramref name="attempt"/> (1-based); the last delay repeats for further attempts.</summary>
    public TimeSpan DelayFor(int attempt)
    {
        if (DelaysSeconds.Length == 0) return TimeSpan.Zero;
        var idx = Math.Clamp(attempt - 1, 0, DelaysSeconds.Length - 1);
        return TimeSpan.FromSeconds(DelaysSeconds[idx]);
    }
}

/// <summary>Default that returns the static <see cref="RetryOptions"/> - used in tests and when no config store is wired.</summary>
public sealed class OptionsRetrySettings(RetryOptions options) : IRetrySettings
{
    private readonly RetryPolicy _policy = new(options.MaxRetries, options.EffectiveDelaysSeconds);
    public ValueTask<RetryPolicy> GetAsync(CancellationToken ct = default) => ValueTask.FromResult(_policy);
}
