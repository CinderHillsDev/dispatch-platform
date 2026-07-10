using Amazon;
using Amazon.Runtime;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Dispatch.Core.Providers;

namespace Dispatch.Providers;

/// <summary>
/// Amazon SES upstream relay via the SES v2 SendEmail API with raw MIME content (spec §8), so attachments +
/// headers are preserved exactly. Settings: AccessKeyId, SecretAccessKey, Region (e.g. us-east-1). Throttling
/// / 5xx map to <see cref="TransientRelayException"/>.
/// </summary>
public sealed class AmazonSesProvider(RelayConfig config) : IRelayProvider
{
    public string Name => "AmazonSes";

    public async Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct)
    {
        var accessKey = ProviderHttp.Require(config, Name, "AccessKeyId");
        var secretKey = ProviderHttp.Require(config, Name, "SecretAccessKey");
        var region = ProviderHttp.Require(config, Name, "Region");

        using var raw = new MemoryStream();
        await message.Message.WriteToAsync(raw, ct);
        raw.Position = 0;

        var request = new SendEmailRequest
        {
            Destination = new Destination { ToAddresses = ProviderHttp.Recipients(message).ToList() },
            Content = new EmailContent { Raw = new RawMessage { Data = raw } },
        };

        using var client = new AmazonSimpleEmailServiceV2Client(
            new BasicAWSCredentials(accessKey, secretKey), RegionEndpoint.GetBySystemName(region));

        try
        {
            var resp = await client.SendEmailAsync(request, ct);
            return RelayResult.Success(resp.MessageId, $"SES MessageId: {resp.MessageId}");
        }
        catch (AmazonServiceException ex) when ((int)ex.StatusCode == 429 || (int)ex.StatusCode >= 500)
        {
            throw new TransientRelayException($"Amazon SES transient error: {ex.Message}", ex);
        }
    }
}
