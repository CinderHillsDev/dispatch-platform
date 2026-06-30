using System.Formats.Tar;
using System.IO.Compression;
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

    // Builds a single cross-platform upgrade package (.tar.gz of manifest.json + .sig + the arch payload).
    private static byte[] Package(RSA key, string arch, byte[] payload, string version = "9.9.9",
        string artifactFile = "payload.tgz", string minFrom = "0.0.0", bool corruptSig = false, string? shaOverride = null)
    {
        var sha = shaOverride ?? Convert.ToHexStringLower(SHA256.HashData(payload));
        var manifest = $"{{\"name\":\"dispatch\",\"version\":\"{version}\",\"minFromVersion\":\"{minFrom}\",\"builtAt\":\"2026-01-01T00:00:00Z\",\"notesUrl\":\"\",\"artifacts\":{{\"{arch}\":{{\"file\":\"{artifactFile}\",\"sha256\":\"{sha}\"}}}}}}";
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        var sig = key.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (corruptSig) sig[0] ^= 0xFF;

        using var outMs = new MemoryStream();
        using (var gz = new GZipStream(outMs, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true))
        {
            Add(tar, "manifest.json", manifestBytes);
            Add(tar, "manifest.json.sig", sig);
            Add(tar, artifactFile, payload);
        }
        return outMs.ToArray();
    }

    private static void Add(TarWriter tar, string name, byte[] data) =>
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });

    private static HttpRequest Upload(byte[] pkg)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.ContentType = "multipart/form-data; boundary=test";
        ctx.Request.Form = new FormCollection(null, new FormFileCollection
        {
            new FormFile(new MemoryStream(pkg), 0, pkg.Length, "package", "dispatch-upgrade.tar.gz"),
        });
        return ctx.Request;
    }

    [Fact]
    public async Task Refuses_when_not_self_managed()
    {
        var (svc, key, _) = Build(selfManaged: false);
        var r = await svc.HandleUploadAsync(Upload(Package(key, UpdateService.CurrentArch, [1, 2, 3])), null, default);
        Assert.False(r.Ok);
        Assert.Equal(409, r.Status);
    }

    [Fact]
    public async Task Rejects_a_bad_signature()
    {
        var (svc, key, _) = Build(selfManaged: true);
        var r = await svc.HandleUploadAsync(Upload(Package(key, UpdateService.CurrentArch, [1, 2, 3], corruptSig: true)), null, default);
        Assert.False(r.Ok);
        Assert.Equal(400, r.Status);
    }

    [Fact]
    public async Task Rejects_a_package_without_this_platform()
    {
        var (svc, key, _) = Build(selfManaged: true);
        var r = await svc.HandleUploadAsync(Upload(Package(key, "bogus-arch", [1, 2, 3])), null, default);
        Assert.False(r.Ok);
        Assert.Contains("platform", r.Message);
    }

    [Fact]
    public async Task Rejects_a_payload_hash_mismatch()
    {
        var (svc, key, _) = Build(selfManaged: true);
        var r = await svc.HandleUploadAsync(Upload(Package(key, UpdateService.CurrentArch, [1, 2, 3], shaOverride: new string('0', 64))), null, default);
        Assert.False(r.Ok);
        Assert.Contains("checksum", r.Message);
    }

    [Fact]
    public async Task Accepts_a_valid_package_then_stages_and_queues_apply()
    {
        var (svc, key, root) = Build(selfManaged: true);
        var payload = Encoding.UTF8.GetBytes("the real self-contained payload tarball bytes");

        var r = await svc.HandleUploadAsync(Upload(Package(key, UpdateService.CurrentArch, payload)), "1.2.3.4", default);

        Assert.True(r.Ok);
        Assert.Equal(202, r.Status);
        Assert.Equal("9.9.9", r.Version);
        var staged = Path.Combine(root, "updates", "staged", "9.9.9");
        Assert.True(File.Exists(Path.Combine(staged, "payload")));
        Assert.True(File.Exists(Path.Combine(staged, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(staged, "manifest.json.sig")));
        Assert.True(File.Exists(Path.Combine(root, "updates", "apply.request")));
        var status = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(root, "updates", "status.json")));
        Assert.Equal("Staged", status.GetProperty("state").GetString());
        // The big package temp file must be cleaned up.
        Assert.False(File.Exists(Path.Combine(root, "updates", "incoming.pkg")));
    }
}
