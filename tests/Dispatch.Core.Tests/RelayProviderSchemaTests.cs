using Dispatch.Core.Providers;
using Dispatch.Core.Relays;

namespace Dispatch.Core.Tests;

public class RelayProviderSchemaTests
{
    [Fact]
    public void GoogleWorkspace_has_no_configurable_fields()
    {
        Assert.Empty(RelayProviderSchema.For(RelayProviderType.GoogleWorkspace));
    }

    [Fact]
    public void Microsoft365_requires_only_a_host()
    {
        var field = Assert.Single(RelayProviderSchema.For(RelayProviderType.Microsoft365));
        Assert.Equal("Host", field.Name);
        Assert.True(field.Required);
        Assert.False(field.Secret);
    }
}
