using Azure.Communication.Email;
using Dispatch.Core.Providers;
using Dispatch.Providers;

namespace Dispatch.Providers.Tests;

public class AzureProviderTests
{
    private static RelayConfig Config(string? mailFrom) => new()
    {
        Provider = RelayProviderType.AzureCommunication,
        Settings = new Dictionary<string, string?>
        {
            ["ConnectionString"] = "endpoint=https://x.communication.azure.com/;accesskey=k",
            ["MailFrom"] = mailFrom,
        },
    };

    // Throws a sentinel the moment a client is requested, proving whether the provider reached the ACS
    // send path (validation passed) or rejected the message first (Create never called).
    private sealed class SentinelException : Exception;
    private sealed class TripwireFactory : IEmailClientFactory
    {
        public bool Created { get; private set; }
        public EmailClient Create(string connectionString) { Created = true; throw new SentinelException(); }
    }

    [Fact]
    public async Task Rejects_sender_not_in_mailfrom_list_without_contacting_acs()
    {
        var factory = new TripwireFactory();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AzureProvider(Config("other@example.org"), factory).SendAsync(ProviderTestSupport.Message(), default));

        Assert.Contains("not a configured MailFrom", ex.Message);
        Assert.False(factory.Created);   // never reached the ACS client
    }

    [Fact]
    public async Task Allows_exact_address_match_and_proceeds_to_send()
    {
        var factory = new TripwireFactory();
        // sender@example.com is the message From in ProviderTestSupport; reaching Create proves it passed validation.
        await Assert.ThrowsAsync<SentinelException>(() =>
            new AzureProvider(Config("sender@example.com"), factory).SendAsync(ProviderTestSupport.Message(), default));
        Assert.True(factory.Created);
    }

    [Fact]
    public async Task Allows_match_against_one_of_several_mailfroms()
    {
        var factory = new TripwireFactory();
        await Assert.ThrowsAsync<SentinelException>(() =>
            new AzureProvider(Config("noreply@example.com, sender@example.com"), factory).SendAsync(ProviderTestSupport.Message(), default));
        Assert.True(factory.Created);
    }

    [Fact]
    public async Task Domain_only_entry_does_not_match_address()
    {
        // ACS has no domain wildcard — a bare domain must NOT grant a same-domain address.
        var factory = new TripwireFactory();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AzureProvider(Config("example.com"), factory).SendAsync(ProviderTestSupport.Message(), default));
        Assert.Contains("not a configured MailFrom", ex.Message);
        Assert.False(factory.Created);
    }

    [Fact]
    public async Task Missing_mailfrom_is_permanent_and_never_contacts_acs()
    {
        var factory = new TripwireFactory();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AzureProvider(Config(null), factory).SendAsync(ProviderTestSupport.Message(), default));
        Assert.Contains("'MailFrom' is not configured", ex.Message);
        Assert.False(factory.Created);
    }
}
