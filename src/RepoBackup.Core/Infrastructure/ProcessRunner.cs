using System.Diagnostics;
using System.Text;

namespace RepoBackup.Core.Infrastructure;

public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public void EnsureSuccess(string operation)
    {
        if (ExitCode != 0) throw new IOException($"{operation} failed (exit {ExitCode}): {Error.Trim()}");
    }
}

public class ProcessRunner
{
    public virtual async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token = default,
        string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null, Action<string>? onOutput = null, string? secret = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null) foreach (var (key, value) in environment) start.Environment[key] = value;
        using var process = new Process { StartInfo = start };
        token.ThrowIfCancellationRequested();
        process.Start();
        using var cancellation = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var stdout = new StringBuilder(); var stderr = new StringBuilder();
        string Redact(string line) => string.IsNullOrEmpty(secret) ? line : line.Replace(secret, "[redacted]", StringComparison.Ordinal);
        async Task ReadAsync(StreamReader reader, StringBuilder target, bool report)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                line = Redact(line);
                target.AppendLine(line);
                if (report) onOutput?.Invoke(line);
            }
        }
        await Task.WhenAll(ReadAsync(process.StandardOutput, stdout, true), ReadAsync(process.StandardError, stderr, false), process.WaitForExitAsync(CancellationToken.None));
        token.ThrowIfCancellationRequested();
        return new(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
