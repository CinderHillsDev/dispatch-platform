namespace Dispatch.Core.Providers;

/// <summary>Builds the appropriate <see cref="IRelayProvider"/> for a <see cref="RelayConfig"/>.</summary>
public interface IRelayProviderFactory
{
    IRelayProvider Build(RelayConfig config);
}
