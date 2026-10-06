using System.Text;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;

namespace RepoBackup.Core.Windows;

public static class HookCommandBuilder
{
    public static string Build(string runnerPath, string providerId, string? dataDirectory = null)
    {
        if (!DiscoveryProviders.HookCapable.Contains(providerId)) throw new ArgumentException("Unknown hook provider.");
        runnerPath = PathSafety.Normalize(runnerPath); dataDirectory = PathSafety.Normalize(dataDirectory ?? AppPaths.DefaultDataDirectory);
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var arguments = WindowsCommandLine.Join(["register-hook", "--source", providerId, "--data-dir", dataDirectory]);
        // EncodedCommand protects paths from both the host's shell and PowerShell interpolation.
        // Start the hidden runner with explicit UTF-8 pipes and a bounded wait.
        var script = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; $process=$null; " +
            "try { [Console]::InputEncoding=[System.Text.UTF8Encoding]::new($false); $hookPayload=[Console]::In.ReadToEnd(); " +
            "if($hookPayload.Length -gt 1048576) { exit 0 }; $start=[System.Diagnostics.ProcessStartInfo]::new(); " +
            "$start.FileName=" + Quote(runnerPath) + "; $start.Arguments=" + Quote(arguments) + "; " +
            "$start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardInput=$true; " +
            "$start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true; " +
            "$process=[System.Diagnostics.Process]::Start($start); $discardOut=$process.StandardOutput.ReadToEndAsync(); $discardError=$process.StandardError.ReadToEndAsync(); " +
            "$writer=[System.IO.StreamWriter]::new($process.StandardInput.BaseStream,[System.Text.UTF8Encoding]::new($false)); " +
            "$writer.Write($hookPayload); $writer.Dispose(); if(-not $process.WaitForExit(4000)) { $process.Kill() } " +
            "} catch { } finally { if($null -ne $process) { $process.Dispose() } }; exit 0";
        return "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }
}
