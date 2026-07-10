namespace Dispatch.Core.Configuration;

/// <summary>Spool directory + worker pool settings (bound from the "Spool" config section).</summary>
public sealed class SpoolOptions
{
    public const string SectionName = "Spool";

    /// <summary>Root spool directory. Created on startup if missing.</summary>
    public string Directory { get; set; } = "./.dispatch-spool";

    /// <summary>Number of concurrent relay workers (clamped to 1..32).</summary>
    public int WorkerCount { get; set; } = 4;
}
