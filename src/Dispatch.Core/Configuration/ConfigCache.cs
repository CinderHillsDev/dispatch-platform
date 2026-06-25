using System.Globalization;
using System.Text.Json;

namespace Dispatch.Core.Configuration;

/// <summary>
/// In-memory snapshot of the SQL <c>config</c> table — the single source of truth for all runtime settings
/// (spec §12.5). Loaded once at startup with one query and refreshed (<see cref="LoadAsync"/>) within the
/// same request that saves a setting, so changes are live without a restart or a file watcher. Workers and
/// services read their settings from here, never from <c>IOptionsMonitor</c> or appsettings.json.
/// </summary>
public sealed class ConfigCache
{
    private volatile IReadOnlyDictionary<string, string> _values =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads (or reloads) the whole config table. Decryption is handled by the repository.</summary>
    public async Task LoadAsync(IConfigRepository repo, CancellationToken ct = default)
    {
        var all = await repo.GetAllAsync(ct);
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in all) d[e.Key] = e.Value;
        _values = d;
    }

    /// <summary>Replaces the cache contents directly. For tests and pre-populated bootstrap scenarios.</summary>
    public void LoadFrom(IReadOnlyDictionary<string, string> values) =>
        _values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);

    public string? GetRaw(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public string GetString(string key, string def) => GetRaw(key) is { } v ? v : def;

    public int GetInt(string key, int def) =>
        int.TryParse(GetRaw(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;

    public long GetLong(string key, long def) =>
        long.TryParse(GetRaw(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;

    public double GetDouble(string key, double def) =>
        double.TryParse(GetRaw(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

    public bool GetBool(string key, bool def) =>
        GetRaw(key) is { } v ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) : def;

    public int[] GetIntArray(string key, int[] def) => TryJson<int[]>(key) ?? def;
    public double[] GetDoubleArray(string key, double[] def) => TryJson<double[]>(key) ?? def;
    public string[] GetStringArray(string key, string[] def) => TryJson<string[]>(key) ?? def;

    private T? TryJson<T>(string key)
    {
        var raw = GetRaw(key);
        if (string.IsNullOrWhiteSpace(raw)) return default;
        try { return JsonSerializer.Deserialize<T>(raw); }
        catch (JsonException) { return default; }
    }

    // ---- Typed section snapshots (spec §12.3) ------------------------------------------------

    public ListenerOptions Listener() => new()
    {
        Ports = GetIntArray(ConfigKeys.ListenerPorts, ListenerOptions.DefaultPorts),
        ServerName = GetString(ConfigKeys.ListenerServerName, "Dispatch"),
        // Empty = allow all; the safe baseline is the seeded default (ConfigDefaults), not a code fallback.
        AllowedCidrs = GetStringArray(ConfigKeys.ListenerAllowedCidrs, []),
        MaxMessageBytes = GetLong(ConfigKeys.ListenerMaxMessageBytes, 0),
        RequireAuth = GetBool(ConfigKeys.ListenerRequireAuth, false),
        // STARTTLS uses the shared TLS certificate (same cert as the HTTPS ingestion API).
        TlsCertPath = GetString(ConfigKeys.TlsCertPath, ""),
        TlsCertPassword = GetString(ConfigKeys.TlsCertPassword, ""),
        ConnectionTimeoutSeconds = GetInt(ConfigKeys.ListenerConnectionTimeoutSeconds, 60),
        MaxConnections = GetInt(ConfigKeys.ListenerMaxConnections, 100),
    };

    public ApiOptions Api() => new()
    {
        Port = GetInt(ConfigKeys.ApiPort, 8025),
        HttpEnabled = GetBool(ConfigKeys.ApiHttpEnabled, true),
        TlsEnabled = GetBool(ConfigKeys.ApiTlsEnabled, false),
        TlsPort = GetInt(ConfigKeys.ApiTlsPort, 8026),
        TlsCertPath = GetString(ConfigKeys.TlsCertPath, ""),
        TlsCertPassword = GetString(ConfigKeys.TlsCertPassword, ""),
        AllowedCidrs = GetStringArray(ConfigKeys.ApiAllowedCidrs, []),
        MaxMessageBytes = GetLong(ConfigKeys.ApiMaxMessageBytes, 0),
        RateLimitPerKey = GetInt(ConfigKeys.ApiRateLimitPerKey, 100),
    };

    /// <summary>Web UI settings. The TLS cert is the one exception that stays in appsettings (spec §12.1),
    /// so it is supplied by the caller, not the config table.</summary>
    public WebUiOptions WebUi() => new()
    {
        Port = GetInt(ConfigKeys.WebUiPort, 8420),
        RequireHttps = GetBool(ConfigKeys.WebUiRequireHttps, true),
    };

    public SpoolOptions Spool() => new()
    {
        Directory = GetString(ConfigKeys.SpoolDirectory, "./.dispatch-spool"),
        WorkerCount = GetInt(ConfigKeys.SpoolWorkerCount, 4),
    };
}
