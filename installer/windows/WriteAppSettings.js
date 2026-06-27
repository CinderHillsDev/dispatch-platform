// Deferred custom action (adapted from the FluxDeploy installer pattern): writes appsettings.json into
// the Dispatch data directory. Per spec §12.1, appsettings holds ONLY the SQL connection string and the
// Web UI TLS cert path — everything else lives in the SQL config table. The admin password is set on first
// run via the dashboard, so it is NOT written here.
// CustomActionData format: "sqlConn|dataDir"
var data = Session.Property("CustomActionData");
var parts = data.split("|");
if (parts.length >= 2) {
    var sqlConn = parts[0].replace(/^\s+|\s+$/g, "");
    var dataDir = parts[1].replace(/^\s+|\s+$/g, "").replace(/\\+$/, "");

    var fso = new ActiveXObject("Scripting.FileSystemObject");
    if (!fso.FolderExists(dataDir)) { fso.CreateFolder(dataDir); }

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
    // default). Remove inherited ACEs and grant only SYSTEM (S-1-5-18) and Administrators (S-1-5-32-544),
    // inheritable so runtime-created files (spool, key) are covered too. Best-effort: never fail the install.
    try {
        var shell = new ActiveXObject("WScript.Shell");
        var icacls = 'icacls "' + dataDir + '" /inheritance:r '
            + '/grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" /T /C';
        shell.Run('cmd /c ' + icacls, 0, true);   // 0 = hidden window, true = wait for completion
    } catch (e) { /* ignore — the per-file key ACL still applies */ }
}
