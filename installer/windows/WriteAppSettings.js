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
}
