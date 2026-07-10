using Dispatch.Core.Configuration;

namespace Dispatch.Core.Tests;

// ConfigCache is the runtime source of truth for every setting; these cover the typed getters, the JSON
// array binding (the documented array-binding gotcha), malformed-value resilience, and snapshot mapping.
public class ConfigCacheTests
{
    private static ConfigCache Cache(params (string, string)[] kv)
    {
        var c = new ConfigCache();
        c.LoadFrom(kv.ToDictionary(x => x.Item1, x => x.Item2));
        return c;
    }

    [Fact]
    public void Typed_getters_parse_values()
    {
        var c = Cache(("i", "42"), ("l", "9000000000"), ("b", "true"), ("s", "hello"));
        Assert.Equal(42, c.GetInt("i", -1));
        Assert.Equal(9000000000L, c.GetLong("l", -1));
        Assert.True(c.GetBool("b", false));
        Assert.Equal("hello", c.GetString("s", "def"));
    }

    [Fact]
    public void Getters_fall_back_on_missing_or_malformed_values()
    {
        var c = Cache(("i", "not-a-number"), ("b", "garbage"));
        Assert.Equal(7, c.GetInt("i", 7));            // unparseable → default
        Assert.Equal(3, c.GetInt("missing", 3));      // absent → default
        Assert.False(c.GetBool("b", true));           // present but not "true" → false (only missing uses default)
        Assert.True(c.GetBool("missing", true));      // absent → default
        Assert.Equal("def", c.GetString("missing", "def"));
    }

    [Fact]
    public void Json_array_binding_parses_and_falls_back()
    {
        var c = Cache(("ports", "[2525,587]"), ("cidrs", "[\"127.0.0.1/32\",\"::1/128\"]"), ("bad", "[1,2,"));
        Assert.Equal([2525, 587], c.GetIntArray("ports", []));
        Assert.Equal(["127.0.0.1/32", "::1/128"], c.GetStringArray("cidrs", []));
        Assert.Equal([9], c.GetIntArray("bad", [9]));         // malformed JSON → default
        Assert.Equal([1], c.GetIntArray("absent", [1]));      // missing → default
    }

    [Fact]
    public void Listener_snapshot_maps_keys_including_auth_toggles()
    {
        var c = Cache(
            (ConfigKeys.ListenerPorts, "[2525]"),
            (ConfigKeys.ListenerServerName, "Relay1"),
            (ConfigKeys.ListenerMaxMessageBytes, "1048576"),
            (ConfigKeys.ListenerRequireAuth, "true"),
            (ConfigKeys.ListenerAllowUnsecureAuth, "true"));

        var l = c.Listener();
        Assert.Equal([2525], l.EffectivePorts);
        Assert.Equal("Relay1", l.ServerName);
        Assert.Equal(1048576, l.MaxMessageBytes);
        Assert.True(l.RequireAuth);
        Assert.True(l.AllowUnsecureAuth);
    }

    [Fact]
    public void AllowUnsecureAuth_defaults_to_false_when_unset()
    {
        Assert.False(new ConfigCache().Listener().AllowUnsecureAuth);
    }
}
