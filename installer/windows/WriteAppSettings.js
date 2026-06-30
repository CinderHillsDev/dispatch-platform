// Deferred custom action (adapted from the FluxDeploy installer pattern): writes appsettings.json into
// the Dispatch data directory. Per spec §12.1, appsettings holds ONLY the SQL connection string and the
// Web UI TLS cert path - everything else lives in the SQL config table. The admin password is set on first
// run via the dashboard, so it is NOT written here.
// CustomActionData format: "sqlConn|dataDir"
var data = Session.Property("CustomActionData");
var parts = data.split("|");
if (parts.length >= 2) {
    var sqlConn = parts[0].replace(/^\s+|\s+$/g, "");
    var dataDir = parts[1].replace(/^\s+|\s+$/g, "").replace(/\\+$/, "");

    var fso = new ActiveXObject("Scripting.FileSystemObject");
    if (!fso.FolderExists(dataDir)) { fso.CreateFolder(dataDir); }

    // Mark this install self-managed so the dashboard exposes the "upload upgrade package" flow.
    var updatesDir = fso.BuildPath(dataDir, "updates");
    if (!fso.FolderExists(updatesDir)) { fso.CreateFolder(updatesDir); }
    var marker = fso.BuildPath(updatesDir, ".self-managed");
    if (!fso.FileExists(marker)) { fso.CreateTextFile(marker, true).Close(); }

    var targetPath = fso.BuildPath(dataDir, "appsettings.json");
    // Don't clobber an existing config (preserves manual edits + upgrades).
    if (!fso.FileExists(targetPath)) {
        // JSON-escape the connection string (backslashes for the .\INSTANCE form, quotes).
        var conn = sqlConn.replace(/\\/g, "\\\\").replace(/"/g, '\\"');
        var json = "{\r\n"
            + '  "ConnectionStrings": {\r\n'
            + '    "DispatchLog": "' + conn + '"\r\n'
            + "  },\r\n"
            + '  "WebUi": {\r\n'
            + '    "TlsCertPath": "",\r\n'
            + '    "TlsCertPassword": ""\r\n'
            + "  },\r\n"
            + '  "Logging": {\r\n'
            + '    "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" }\r\n'
            + "  }\r\n"
            + "}";
        var stream = fso.CreateTextFile(targetPath, true);
        stream.Write(json);
        stream.Close();
    }

    // Lock down the data directory so the connection string, spool (message bodies), the .dispatch-key
    // encryption key, and logs aren't readable by other local users (ProgramData is world-readable by
    // default). Approach that can NEVER lock out the service: convert inherited ACEs to explicit
    // (/inheritance:d), then remove ONLY BUILTIN\Users (S-1-5-32-545) and Authenticated Users (S-1-5-11).
    // SYSTEM + Administrators (the service runs as LocalSystem) are left untouched, so the service keeps
    // access. Best-effort: never fail the install.
    try {
        var shell = new ActiveXObject("WScript.Shell");
        shell.Run('cmd /c icacls "' + dataDir + '" /inheritance:d /T /C', 0, true);
        shell.Run('cmd /c icacls "' + dataDir + '" /remove:g "*S-1-5-32-545" "*S-1-5-11" /T /C', 0, true);
    } catch (e) { /* ignore - the per-file key ACL still applies */ }
}
