namespace Dispatch.Core.Platform;

/// <summary>
/// Detects Azure by probing the Instance Metadata Service (IMDS) at the link-local address 169.254.169.254
/// with the required <c>Metadata: true</c> header. The result is cached for the process lifetime. Fails
/// <b>closed</b> (reports not-Azure) on any error or timeout, so a transient hiccup - or an ordinary non-Azure
/// host where IMDS is simply unreachable - never wrongly restricts the product. The probe uses no proxy
/// (IMDS is link-local) and a short timeout so a non-Azure host returns quickly.
/// </summary>
public sealed class AzureImdsEnvironment : ICloudEnvironment
{
    private static readonly Uri ImdsUri = new("http://169.254.169.254/metadata/instance?api-version=2021-02-01");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool? _isAzure;

    public async ValueTask<bool> IsAzureAsync(CancellationToken ct = default)
    {
        if (_isAzure is { } cached) return cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_isAzure is { } cached2) return cached2;
            _isAzure = await ProbeAsync(ct);
            return _isAzure.Value;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<bool> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            using var req = new HttpRequestMessage(HttpMethod.Get, ImdsUri);
            req.Headers.Add("Metadata", "true");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(3));

            using var resp = await http.SendAsync(req, linked.Token);
            if (!resp.IsSuccessStatusCode) return false;

            // A genuine Azure IMDS response carries the compute block naming the Azure environment/provider.
            var body = await resp.Content.ReadAsStringAsync(linked.Token);
            return body.Contains("azEnvironment", StringComparison.OrdinalIgnoreCase)
                || body.Contains("Microsoft.Compute", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;   // not Azure, or IMDS unreachable - either way, do not restrict.
        }
    }
}
