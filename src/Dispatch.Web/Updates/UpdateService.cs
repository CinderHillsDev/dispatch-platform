using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dispatch.Core.Audit;
using Dispatch.Core.Configuration;
using Dispatch.Core.Updates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Dispatch.Web.Updates;

/// <summary>
/// The cross-platform half of web-UI self-update: accepts an uploaded upgrade package, authenticates it
/// (signature + payload hash via <see cref="UpdateBundleVerifier"/>), checks arch/version compatibility,
/// stages it under the content root, and hands off an <see cref="ApplyRequest"/> to the platform updater
/// (Linux systemd/bash or the Windows helper) which does the actual swap + restart + rollback and writes
/// back <see cref="UpdateStatus"/>. Identical on every install type; only the platform updater differs.
/// </summary>
public sealed class UpdateService(IWebHostEnvironment env, IConfigRepository config, IAuditLog audit, UpdateBundleVerifier verifier)
{
    private static readonly JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };

    private string UpdatesDir => Path.Combine(env.ContentRootPath, "updates");

    public static string CurrentVersion => typeof(UpdateService).Assembly.GetName().Version?.ToString() ?? "dev";

    public static string CurrentArch =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" +
        (RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            var a => a.ToString().ToLowerInvariant(),
        });

    public async Task<bool> IsSelfManagedAsync(CancellationToken ct) =>
        string.Equals(await config.GetAsync(ConfigKeys.UpdatesSelfManaged, ct), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Current version + arch, whether in-app updates are available here, and the latest updater status.</summary>
    public async Task<object> StatusAsync(CancellationToken ct)
    {
        UpdateStatus? status = null;
        var path = Path.Combine(UpdatesDir, "status.json");
        try { if (File.Exists(path)) status = JsonSerializer.Deserialize<UpdateStatus>(await File.ReadAllTextAsync(path, ct), Json); }
        catch { /* a partially-written status file just reads as Idle */ }

        return new
        {
            currentVersion = CurrentVersion,
            arch = CurrentArch,
            selfManaged = await IsSelfManagedAsync(ct),
            state = (status?.State ?? UpdateState.Idle).ToString(),
            message = status?.Message ?? "",
            stagedVersion = status?.Version,
            updatedAtUtc = status?.UpdatedAtUtc,
        };
    }

    public sealed record UploadResult(bool Ok, int Status, string Message, string? Version = null);

    /// <summary>Verify + stage an uploaded bundle (multipart: bundle / manifest / signature) and queue it.</summary>
    public async Task<UploadResult> HandleUploadAsync(HttpRequest req, string? sourceIp, CancellationToken ct)
    {
        if (!await IsSelfManagedAsync(ct))
            return new(false, StatusCodes.Status409Conflict,
                "In-app updates aren't available on this install type - update via your platform's normal method.");
        if (!req.HasFormContentType)
            return new(false, StatusCodes.Status400BadRequest, "Expected a multipart upload with 'bundle', 'manifest', and 'signature'.");

        var form = await req.ReadFormAsync(ct);
        var bundle = form.Files["bundle"];
        var manifestFile = form.Files["manifest"];
        var sigFile = form.Files["signature"];
        if (bundle is null || manifestFile is null || sigFile is null)
            return new(false, StatusCodes.Status400BadRequest, "Upload must include 'bundle' (.tar.gz), 'manifest' (.json), and 'signature' (.sig).");

        var manifestBytes = await ReadAllAsync(manifestFile, ct);
        var sigBytes = await ReadAllAsync(sigFile, ct);

        // 1) Authenticate the manifest signature against the embedded release key (fail-closed).
        if (!verifier.VerifyManifestSignature(manifestBytes, sigBytes))
            return await RejectAsync("the signature is invalid (not signed by the Dispatch release key)", sourceIp, ct);

        UpdateManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, Json); }
        catch { manifest = null; }
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
            return await RejectAsync("the manifest is unreadable", sourceIp, ct);

        // 2) Compatibility: arch must match this host; this version must satisfy the bundle's minimum.
        if (!string.Equals(manifest.Arch, CurrentArch, StringComparison.OrdinalIgnoreCase))
            return await RejectAsync($"bundle arch '{manifest.Arch}' does not match this host ('{CurrentArch}')", sourceIp, ct);
        if (!VersionAtLeast(CurrentVersion, manifest.MinFromVersion))
            return await RejectAsync($"this version ({CurrentVersion}) is older than the bundle's minimum ({manifest.MinFromVersion})", sourceIp, ct);

        // 3) Stage the payload and confirm its hash matches the (now-trusted) manifest.
        var stagedDir = Path.Combine(UpdatesDir, "staged", manifest.Version);
        TryDelete(stagedDir);
        Directory.CreateDirectory(stagedDir);
        var bundlePath = Path.Combine(stagedDir, "bundle.tar.gz");
        await using (var fs = File.Create(bundlePath)) await bundle.CopyToAsync(fs, ct);
        await using (var read = File.OpenRead(bundlePath))
            if (!UpdateBundleVerifier.VerifyPayloadHash(read, manifest.Sha256))
            {
                TryDelete(stagedDir);
                return await RejectAsync("the payload checksum does not match the manifest", sourceIp, ct);
            }
        await File.WriteAllBytesAsync(Path.Combine(stagedDir, "manifest.json"), manifestBytes, ct);
        await File.WriteAllBytesAsync(Path.Combine(stagedDir, "manifest.json.sig"), sigBytes, ct);

        // 4) Hand off to the platform updater: status -> Staged, then drop the apply request atomically.
        await WriteStatusAsync(new UpdateStatus(UpdateState.Staged, manifest.Version, "Upgrade staged; applying…", DateTime.UtcNow), ct);
        var request = new ApplyRequest(manifest.Version, manifest.Arch, stagedDir, CurrentVersion, DateTime.UtcNow);
        var reqPath = Path.Combine(UpdatesDir, "apply.request");
        var tmp = reqPath + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(request, Json), ct);
        File.Move(tmp, reqPath, overwrite: true);

        await audit.Lifecycle("Update requested",
            $"Upgrade to {manifest.Version} ({manifest.Arch}) uploaded, verified, and handed to the updater.", "Notice");
        return new(true, StatusCodes.Status202Accepted,
            $"Verified and staged {manifest.Version}; the updater will apply it and the dashboard will briefly restart.", manifest.Version);
    }

    private async Task<UploadResult> RejectAsync(string why, string? ip, CancellationToken ct)
    {
        await audit.Audit("Update", $"Rejected an uploaded upgrade bundle: {why}.", "Warning", "admin", ip, ct: ct);
        return new(false, StatusCodes.Status400BadRequest, $"Upgrade rejected: {why}.");
    }

    private async Task WriteStatusAsync(UpdateStatus s, CancellationToken ct)
    {
        Directory.CreateDirectory(UpdatesDir);
        await File.WriteAllTextAsync(Path.Combine(UpdatesDir, "status.json"), JsonSerializer.Serialize(s, Json), ct);
    }

    private static async Task<byte[]> ReadAllAsync(IFormFile file, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static void TryDelete(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best-effort */ } }

    /// <summary>True if <paramref name="version"/> &gt;= <paramref name="minimum"/> (dotted numeric); permissive on parse failure.</summary>
    internal static bool VersionAtLeast(string version, string minimum) =>
        !Version.TryParse(Core(version), out var v) || !Version.TryParse(Core(minimum), out var m) || v >= m;

    private static string Core(string? s)
    {
        var head = (s ?? "").Split('-')[0];
        return head.Contains('.') ? head : head + ".0";   // Version needs at least major.minor
    }
}
