using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dispatch.Core.Configuration;
using Dispatch.Core.Updates;
using Dispatch.Web.Updates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace Dispatch.Web.Tests;

public class UpdateServiceTests
{
    private sealed class FakeEnv : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "test";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
    }

    private static (UpdateService Svc, RSA Key, string Root) Build(bool selfManaged)
    {
        var root = Directory.CreateTempSubdirectory("dispatch-updatesvc").FullName;
        var config = new FakeConfigRepository();
        if (selfManaged) config.SetAsync(ConfigKeys.UpdatesSelfManaged, "true").GetAwaiter().GetResult();
        var rsa = RSA.Create(3072);
        var svc = new UpdateService(new FakeEnv { ContentRootPath = root }, config, new FakeAuditLog(),
            new UpdateBundleVerifier(rsa.ExportSubjectPublicKeyInfoPem()));
        return (svc, rsa, root);
    }

    private static (byte[] Manifest, byte[] Sig) SignedManifest(RSA key, string arch, string version, string sha256, string minFrom = "0.0.0")
    {
        var json = $"{{\"name\":\"dispatch\",\"version\":\"{version}\",\"arch\":\"{arch}\",\"sha256\":\"{sha256}\",\"minFromVersion\":\"{minFrom}\",\"builtAt\":\"2026-01-01T00:00:00Z\",\"notesUrl\":\"\"}}";
        var bytes = Encoding.UTF8.GetBytes(json);
        return (bytes, key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private static HttpRequest Multipart(byte[] bundle, byte[] manifest, byte[] sig)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.ContentType = "multipart/form-data; boundary=test";
        ctx.Request.Form = new FormCollection(null, new FormFileCollection
        {
            new FormFile(new MemoryStream(bundle), 0, bundle.Length, "bundle", "bundle.tar.gz"),
            new FormFile(new MemoryStream(manifest), 0, manifest.Length, "manifest", "manifest.json"),
            new FormFile(new MemoryStream(sig), 0, sig.Length, "signature", "manifest.json.sig"),
        });
        return ctx.Request;
    }

    [Fact]
    public async Task Refuses_when_not_self_managed()
    {
        var (svc, key, _) = Build(selfManaged: false);
        var (m, s) = SignedManifest(key, UpdateService.CurrentArch, "9.9.9", "deadbeef");
        var r = await svc.HandleUploadAsync(Multipart([1], m, s), null, default);
        Assert.False(r.Ok);
        Assert.Equal(409, r.Status);
    }

    [Fact]
    public async Task Rejects_a_bad_signature()
    {
        var (svc, key, _) = Build(selfManaged: true);
        var (m, _) = SignedManifest(key, UpdateService.CurrentArch, "9.9.9", "deadbeef");
        var r = await svc.HandleUploadAsync(Multipart([1], m, new byte[64]), null, default);
        Assert.False(r.Ok);
        Assert.Equal(400, r.Status);
    }

    [Fact]
    public async Task Rejects_a_mismatched_arch()
    {
        var (svc, key, _) = Build(selfManaged: true);
        var bundle = Encoding.UTF8.GetBytes("payload");
        var (m, s) = SignedManifest(key, "bogus-arch", "9.9.9", Convert.ToHexStringLower(SHA256.HashData(bundle)));
        var r = await svc.HandleUploadAsync(Multipart(bundle, m, s), null, default);
        Assert.False(r.Ok);
        Assert.Contains("arch", r.Message);
    }

    [Fact]
    public async Task Rejects_a_payload_hash_mismatch()
    {
        var (svc, key, _) = Build(selfManaged: true);
        var bundle = Encoding.UTF8.GetBytes("payload");
        var (m, s) = SignedManifest(key, UpdateService.CurrentArch, "9.9.9", new string('0', 64));
        var r = await svc.HandleUploadAsync(Multipart(bundle, m, s), null, default);
        Assert.False(r.Ok);
        Assert.Contains("checksum", r.Message);
    }

    [Fact]
    public async Task Accepts_a_valid_bundle_then_stages_and_queues_apply()
    {
        var (svc, key, root) = Build(selfManaged: true);
        var bundle = Encoding.UTF8.GetBytes("the real payload tarball bytes");
        var (m, s) = SignedManifest(key, UpdateService.CurrentArch, "9.9.9", Convert.ToHexStringLower(SHA256.HashData(bundle)));

        var r = await svc.HandleUploadAsync(Multipart(bundle, m, s), "1.2.3.4", default);

        Assert.True(r.Ok);
        Assert.Equal(202, r.Status);
        Assert.Equal("9.9.9", r.Version);
        var staged = Path.Combine(root, "updates", "staged", "9.9.9");
        Assert.True(File.Exists(Path.Combine(staged, "bundle.tar.gz")));
        Assert.True(File.Exists(Path.Combine(staged, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(staged, "manifest.json.sig")));
        Assert.True(File.Exists(Path.Combine(root, "updates", "apply.request")));
        var status = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(root, "updates", "status.json")));
        Assert.Equal("Staged", status.GetProperty("state").GetString());
    }
}
