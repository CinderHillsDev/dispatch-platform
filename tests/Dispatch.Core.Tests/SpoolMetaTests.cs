using Dispatch.Core.Spool;

namespace Dispatch.Core.Tests;

public class SpoolMetaTests
{
    [Fact]
    public void Save_then_Load_round_trips_all_fields()
    {
        using var t = new TempSpool();
        var (emlPath, id) = TestData.Seed(t.Spool.IncomingDir, t.Spool,
            from: "a@x.com", to: ["b@y.com", "c@y.com"], retryCount: 2);

        var loaded = SpoolMeta.Load(emlPath);

        Assert.Equal(id, loaded.SpoolId);
        Assert.Equal("a@x.com", loaded.FromAddress);
        Assert.Equal(["b@y.com", "c@y.com"], loaded.ToAddresses);
        Assert.Equal(2, loaded.RetryCount);
    }

    [Fact]
    public void Peek_returns_null_when_meta_missing()
    {
        using var t = new TempSpool();
        var emlPath = Path.Combine(t.Spool.IncomingDir, $"{Guid.NewGuid()}.eml");
        File.WriteAllText(emlPath, "raw");   // .eml present, no .meta

        Assert.Null(SpoolMeta.Peek(emlPath));
    }

    [Fact]
    public void Peek_returns_null_when_meta_corrupt()
    {
        using var t = new TempSpool();
        var emlPath = Path.Combine(t.Spool.IncomingDir, $"{Guid.NewGuid()}.eml");
        File.WriteAllText(emlPath, "raw");
        File.WriteAllText(SpoolMeta.PathFor(emlPath), "{ this is not valid json ");

        Assert.Null(SpoolMeta.Peek(emlPath));
    }

    [Fact]
    public void PathFor_swaps_extension_to_meta()
    {
        Assert.Equal(
            Path.Combine("dir", "abc.meta"),
            SpoolMeta.PathFor(Path.Combine("dir", "abc.eml")));
    }
}
