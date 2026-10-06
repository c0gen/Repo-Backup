using System.Diagnostics;
using System.Text;
using RepoBackup.Core.Infrastructure;

namespace RepoBackup.Tests;

public static class NativeProcessFixture
{
    public static string ProgramPath(string project)
    {
        var configuration = Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.Name;
        var path = Path.Combine(Environment.CurrentDirectory, "src", project, "bin", configuration, "net10.0-windows", project == "RepoBackup.Desktop" ? "RepoBackup.exe" : project + ".exe");
        if (!File.Exists(path)) throw new FileNotFoundException("Build the solution before running native command tests.", path);
        return path;
    }

    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, string? input = null, int timeoutSeconds = 15)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start fixture command.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var cancellation = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
        if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
        process.StandardInput.Close();
        await process.WaitForExitAsync(timeout.Token);
        return new(process.ExitCode, await output, await error);
    }
}
