namespace Dispatch.Core.Platform;

/// <summary>
/// Detects the hosting cloud so the product can adapt to platform constraints - notably that Azure blocks
/// outbound TCP port 25, which would make an SMTP relay configured to deliver on port 25 fail silently.
/// </summary>
public interface ICloudEnvironment
{
    /// <summary>True when running on an Azure VM. Detected once (via the Instance Metadata Service) and cached.</summary>
    ValueTask<bool> IsAzureAsync(CancellationToken ct = default);
}
