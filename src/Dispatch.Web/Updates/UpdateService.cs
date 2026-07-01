using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dispatch.Core.Audit;
using Dispatch.Core.Configuration;
using Dispatch.Core.Updates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Dispatch.Web.Updates;

/// <summary>
/// The cross-platform half of web-UI self-update. Accepts ONE uploaded upgrade package (a .tar.gz carrying
/// manifest.json + manifest.json.sig + a self-contained payload per platform), authenticates it (signature
/// over the manifest via the embedded release key), selects the payload matching THIS host's arch, verifies
/// that payload's SHA-256, stages it under the content root, and hands an <see cref="ApplyRequest"/> to the
/// platform updater (Linux systemd/bash or the Windows helper) which does the swap + restart + rollback and
/// writes back <see cref="UpdateStatus"/>. The same package works on every install; only the apply differs.
/// </summary>
public sealed class UpdateService(IWebHostEnvironment env, IConfigRepository config, IAuditLog audit, UpdateBundleVerifier verifier, ILogger<UpdateService>? log = null)
{
    private static readonly JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };

    // Upper bound for an uploaded upgrade package. Generous (a payload is a self-contained runtime for one
    // arch, ~150-200 MB) but not unbounded, so a bad or hostile upload can't fill the disk unchecked.
    private const long MaxUploadBytes = 2L * 1024 * 1024 * 1024; // 2 GiB

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

    /// <summary>In-app updates apply where a platform updater ships: the installer drops a marker, or the
    /// SQL flag is set. Docker/unknown installs have neither and refuse uploads.</summary>
    public async Task<bool> IsSelfManagedAsync(CancellationToken ct)
    {
        if (File.Exists(Path.Combine(UpdatesDir, ".self-managed"))) return true;
        return string.Equals(await config.GetAsync(ConfigKeys.UpdatesSelfManaged, ct), "true", StringComparison.OrdinalIgnoreCase);
    }

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

    /// <summary>Verify the uploaded package, select this host's payload, and queue it for the updater.</summary>
    public async Task<UploadResult> HandleUploadAsync(HttpRequest req, string? sourceIp, CancellationToken ct)
    {
        if (!await IsSelfManagedAsync(ct))
            return new(false, StatusCodes.Status409Conflict,
                "In-app updates aren't available on this install type - update via your platform's normal method.");
        if (!req.HasFormContentType)
            return new(false, StatusCodes.Status400BadRequest, "Expected a multipart upload with the upgrade package in field 'package'.");

        // Upgrade packages are large (hundreds of MB), so lift this request's body-size caps BEFORE reading
        // the form - otherwise ReadFormAsync throws and the upload fails with a 500: Kestrel's
        // MaxRequestBodySize defaults to ~30 MB and the multipart body-length limit to 128 MB. Safe here: the
        // endpoint is admin-only and gated to self-managed installs (checked above), and we cap at
        // MaxUploadBytes rather than removing the limit entirely. Skip when the form is already parsed or
        // supplied (e.g. a pre-populated Request.Form in tests) so we don't clobber it.
        if (req.HttpContext.Features.Get<IFormFeature>()?.Form is null)
        {
            var sizeFeature = req.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = MaxUploadBytes;
            req.HttpContext.Features.Set<IFormFeature>(
                new FormFeature(req, new FormOptions { MultipartBodyLengthLimit = MaxUploadBytes }));
        }

        IFormCollection form;
        try { form = await req.ReadFormAsync(ct); }
        catch (Exception ex) when (ex is BadHttpRequestException or InvalidDataException)
        {
            log?.LogWarning(ex, "Upgrade upload rejected while reading the multipart body");
            return new(false, StatusCodes.Status413PayloadTooLarge,
                $"The upload could not be read - it may exceed the {MaxUploadBytes / (1024 * 1024)} MB limit or be malformed.");
        }
        var pkg = form.Files["package"] ?? (form.Files.Count == 1 ? form.Files[0] : null);
        if (pkg is null)
            return new(false, StatusCodes.Status400BadRequest, "Upload the single upgrade package (.tar.gz) in field 'package'.");

        Directory.CreateDirectory(UpdatesDir);
        var pkgPath = Path.Combine(UpdatesDir, "incoming.pkg");
        await using (var fs = File.Create(pkgPath)) await pkg.CopyToAsync(fs, ct);

        try
        {
            // 1) Authenticate the manifest signature against the embedded release key (fail-closed).
            var manifestBytes = await ReadEntryAsync(pkgPath, "manifest.json", ct);
            var sigBytes = await ReadEntryAsync(pkgPath, "manifest.json.sig", ct);
            if (manifestBytes is null || sigBytes is null)
                return await RejectAsync("the package is missing manifest.json / manifest.json.sig", sourceIp, ct);
            if (!verifier.VerifyManifestSignature(manifestBytes, sigBytes))
                return await RejectAsync("the signature is invalid (not signed by the Dispatch release key)", sourceIp, ct);

            UpdateManifest? manifest;
            try { manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, Json); }
            catch { manifest = null; }
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
                return await RejectAsync("the manifest is unreadable", sourceIp, ct);

            // 2) Compatibility: must carry a payload for this host's arch, and satisfy minFromVersion.
            if (manifest.Artifacts is null || !manifest.Artifacts.TryGetValue(CurrentArch, out var art) || art is null)
                return await RejectAsync($"the package has no payload for this platform ({CurrentArch})", sourceIp, ct);
            if (!VersionAtLeast(CurrentVersion, manifest.MinFromVersion))
                return await RejectAsync($"this version ({CurrentVersion}) is older than the package minimum ({manifest.MinFromVersion})", sourceIp, ct);

            // 3) Extract THIS arch's payload to the staging dir and confirm its sha256 matches the manifest.
            var stagedDir = Path.Combine(UpdatesDir, "staged", manifest.Version);
            TryDelete(stagedDir);
            Directory.CreateDirectory(stagedDir);
            var payloadPath = Path.Combine(stagedDir, "payload");
            var sha = await ExtractAndHashAsync(pkgPath, art.File, payloadPath, ct);
            if (sha is null) { TryDelete(stagedDir); return await RejectAsync($"payload '{art.File}' not found in the package", sourceIp, ct); }
            if (!HashEquals(sha, art.Sha256)) { TryDelete(stagedDir); return await RejectAsync("the payload checksum does not match the manifest", sourceIp, ct); }

            await File.WriteAllBytesAsync(Path.Combine(stagedDir, "manifest.json"), manifestBytes, ct);
            await File.WriteAllBytesAsync(Path.Combine(stagedDir, "manifest.json.sig"), sigBytes, ct);

            // 4) Hand off to the platform updater.
            await WriteStatusAsync(new UpdateStatus(UpdateState.Staged, manifest.Version, "Upgrade staged; applying...", DateTime.UtcNow), ct);
            var request = new ApplyRequest(manifest.Version, CurrentArch, stagedDir, CurrentVersion, DateTime.UtcNow);
            var reqPath = Path.Combine(UpdatesDir, "apply.request");
            var tmp = reqPath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(request, Json), ct);
            File.Move(tmp, reqPath, overwrite: true);

            TriggerPlatformApply();

            await audit.Lifecycle("Update requested",
                $"Upgrade to {manifest.Version} ({CurrentArch}) uploaded, verified, and handed to the updater.", "Notice");
            return new(true, StatusCodes.Status202Accepted,
                $"Verified and staged {manifest.Version}; the updater will apply it and the dashboard will briefly restart.", manifest.Version);
        }
        finally { try { File.Delete(pkgPath); } catch { /* best-effort */ } }
    }

    // Reads a single (small) entry's bytes from the .tar.gz package. Entry names may be prefixed "./".
    private static async Task<byte[]?> ReadEntryAsync(string packagePath, string name, CancellationToken ct)
    {
        await using var fs = File.OpenRead(packagePath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        await using var tar = new TarReader(gz);
        TarEntry? e;
        while ((e = await tar.GetNextEntryAsync(cancellationToken: ct)) is not null)
        {
            if (Normalize(e.Name) == name && e.DataStream is not null)
            {
                using var ms = new MemoryStream();
                await e.DataStream.CopyToAsync(ms, ct);
                return ms.ToArray();
            }
        }
        return null;
    }

    // Streams a (possibly large) payload entry to disk while computing its SHA-256 (hex). Null if not found.
    private static async Task<string?> ExtractAndHashAsync(string packagePath, string name, string destPath, CancellationToken ct)
    {
        await using var fs = File.OpenRead(packagePath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        await using var tar = new TarReader(gz);
        TarEntry? e;
        while ((e = await tar.GetNextEntryAsync(cancellationToken: ct)) is not null)
        {
            if (Normalize(e.Name) == name && e.DataStream is not null)
            {
                using var sha = SHA256.Create();
                await using var outFs = File.Create(destPath);
                await using var crypto = new CryptoStream(outFs, sha, CryptoStreamMode.Write);
                await e.DataStream.CopyToAsync(crypto, ct);
                await crypto.FlushFinalBlockAsync(ct);
                return Convert.ToHexStringLower(sha.Hash!);
            }
        }
        return null;
    }

    private static string Normalize(string entryName) => entryName.TrimStart('.', '/');

    private static bool HashEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(a.Trim().ToLowerInvariant()), Encoding.ASCII.GetBytes((b ?? "").Trim().ToLowerInvariant()));

    private async Task<UploadResult> RejectAsync(string why, string? ip, CancellationToken ct)
    {
        await audit.Audit("Update", $"Rejected an uploaded upgrade package: {why}.", "Warning", "admin", ip, ct: ct);
        return new(false, StatusCodes.Status400BadRequest, $"Upgrade rejected: {why}.");
    }

    private async Task WriteStatusAsync(UpdateStatus s, CancellationToken ct)
    {
        Directory.CreateDirectory(UpdatesDir);
        await File.WriteAllTextAsync(Path.Combine(UpdatesDir, "status.json"), JsonSerializer.Serialize(s, Json), ct);
    }

    // Linux: the systemd dispatch-update.path watcher picks up apply.request - nothing to do. Windows: there
    // is no path watcher, so write the embedded updater script to the data dir and launch it DECOUPLED as
    // SYSTEM via Task Scheduler, so it survives the service restart it performs.
    private void TriggerPlatformApply()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var script = Path.Combine(UpdatesDir, "dispatch-update.ps1");
            File.WriteAllText(script, EmbeddedWindowsUpdater());
            Schtasks("/Create", "/TN", "DispatchUpdate", "/TR",
                $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{script}\"",   // quote: data dir may contain spaces
                "/SC", "ONCE", "/ST", "00:00", "/RU", "SYSTEM", "/RL", "HIGHEST", "/F");
            Schtasks("/Run", "/TN", "DispatchUpdate");
        }
        catch (Exception ex) { log?.LogError(ex, "Failed to launch the Windows updater task"); }
    }

    private static void Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        p?.WaitForExit(15000);
    }

    private static string EmbeddedWindowsUpdater()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith("dispatch-update-windows.ps1", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Embedded Windows updater script not found.");
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
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
