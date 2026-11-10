using System.Diagnostics;
using System.Text;

namespace RepoBackup.Core.Infrastructure;

public partial class ProcessRunner
{
    public virtual async Task<ProcessResult> RunStreamAsync(string executable, IEnumerable<string> arguments,
        Func<Stream, CancellationToken, Task> consume, CancellationToken token = default,
        IReadOnlyDictionary<string, string?>? environment = null, string? secret = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Environment.CurrentDirectory
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null) foreach (var (key, value) in environment)
        {
            if (value is null) start.Environment.Remove(key); else start.Environment[key] = value;
        }
        using var process = new Process { StartInfo = start };
        token.ThrowIfCancellationRequested(); process.Start();
        void Kill() { try { if (!process.HasExited) process.Kill(true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
        using var cancellation = token.Register(Kill);
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await consume(process.StandardOutput.BaseStream, token);
            await process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, token);
            await process.WaitForExitAsync(CancellationToken.None);
            token.ThrowIfCancellationRequested();
            var stderr = await error;
            if (!string.IsNullOrEmpty(secret)) stderr = stderr.Replace(secret, "[redacted]", StringComparison.Ordinal);
            return new(process.ExitCode, "", stderr);
        }
        catch
        {
            Kill();
            await process.WaitForExitAsync(CancellationToken.None);
            await error;
            token.ThrowIfCancellationRequested();
            throw;
        }
    }

    public async Task<ProcessResult> RunRawAsync(string executable, IEnumerable<string> arguments, CancellationToken token = default,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        string output = "";
        var result = await RunStreamAsync(executable, arguments, async (stream, cancellation) =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            output = await reader.ReadToEndAsync(cancellation);
        }, token, environment);
        return result with { Output = output };
    }
}
