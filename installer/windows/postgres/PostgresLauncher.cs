using System;
using System.Diagnostics;
using System.IO;

// Thin launcher: a WiX Burn ExePackage runs an EXE, not a .ps1, so this wrapper invokes
// InstallPostgres.ps1 (shipped alongside it as a bundle payload). Arg 0 = DB name.
class Program
{
    static int Main(string[] args)
    {
        var scriptDir = AppDomain.CurrentDomain.BaseDirectory;
        var scriptPath = Path.Combine(scriptDir, "InstallPostgres.ps1");
        if (!File.Exists(scriptPath))
        {
            Console.Error.WriteLine($"Script not found: {scriptPath}");
            return 1;
        }

        var dbName = args.Length > 0 ? args[0] : "DispatchLog";
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {dbName}",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var process = Process.Start(psi);
        process.WaitForExit();
        return process.ExitCode;
    }
}
