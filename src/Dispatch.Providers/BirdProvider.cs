using System.Net.Http;
using System.Text.Json;
using Dispatch.Core.Providers;

namespace Dispatch.Providers;

/// <summary>
/// Bird (formerly SparkPost/MessageBird) upstream relay via the Channels API (spec §8). Structured JSON send
/// to a workspace's email channel — the channel defines the sender. Settings: AccessKey, WorkspaceId,
/// ChannelId. Auth via <c>Authorization: AccessKey &lt;token&gt;</c>. Returns 202 with { id }.
/// Note: attachments are not forwarded (Bird takes hosted media URLs, not inline bytes).
/// </summary>
public sealed class BirdProvider(RelayConfig config, HttpClient http) : IRelayProvider
{
    public string Name => "Bird";

    public async Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct)
    {
        // Bird's dashboard calls this an "API key"; the wire header still uses the AccessKey scheme.
        var apiKey = ProviderHttp.Require(config, Name, "ApiKey");
        var workspaceId = ProviderHttp.Require(config, Name, "WorkspaceId");
        var channelId = ProviderHttp.Require(config, Name, "ChannelId");
        var html = ProviderHttp.Html(message);
        var text = ProviderHttp.Text(message);

        var contacts = ProviderHttp.Recipients(message)
            .Select(r => new Dictionary<string, object?> { ["identifierKey"] = "emailaddress", ["identifierValue"] = r })
            .ToArray();

        var body = new Dictionary<string, object?>
        {
            ["receiver"] = new Dictionary<string, object?> { ["contacts"] = contacts },
            ["body"] = new Dictionary<string, object?>
            {
                ["type"] = "html",
                ["html"] = new Dictionary<string, object?> { ["html"] = html ?? text ?? "", ["text"] = text ?? "" },
                ["metadata"] = new Dictionary<string, object?>
                {
                    ["html"] = new Dictionary<string, object?> { ["subject"] = ProviderHttp.Subject(message) },
                },
            },
        };

        var url = $"https://api.bird.com/workspaces/{Uri.EscapeDataString(workspaceId)}/channels/{Uri.EscapeDataString(channelId)}/messages";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = ProviderHttp.Json(body) };
        req.Headers.Add("Authorization", $"AccessKey {apiKey}");

        var (status, json) = await ProviderHttp.SendAsync(http, req, Name, ct);

        // Success is 202 with { "id": "<uuid>" }.
        string? id = null;
        try { if (JsonDocument.Parse(json).RootElement.TryGetProperty("id", out var m)) id = m.GetString(); }
        catch { /* best-effort */ }

        return RelayResult.Success(id, $"HTTP {status} — Bird id: {id}");
    }
}
