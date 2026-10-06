using System.Text;
using System.Text.Json;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Storage;

namespace RepoBackup.Core.Windows;

public static class HookRegistration
{
    public static List<string> Parse(string provider, string json)
    {
        if (!DiscoveryProviders.All.Contains(provider)) throw new ArgumentException("Unknown hook provider.");
        // Windows PowerShell 5.1 may prepend a UTF-8 BOM to a redirected native
        // stream. Console.In exposes it as a character rather than stripping it.
        using var document = JsonDocument.Parse(json.TrimStart('\uFEFF')); var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid hook input.");
        if (provider == DiscoveryProviders.Antigravity)
        {
            if (!root.TryGetProperty("workspacePaths", out var paths) || paths.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Invalid hook workspace paths.");
            return paths.EnumerateArray().Select(p => PathSafety.Normalize(p.GetString() ?? "")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        if (!root.TryGetProperty("cwd", out var cwd)) throw new InvalidDataException("Hook input has no working directory.");
        return [PathSafety.Normalize(cwd.GetString() ?? "")];
    }

    public static async Task<bool> RegisterAsync(string provider, AppPaths paths, TextReader input, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var text = new StringBuilder(); var buffer = new char[4096]; int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (text.Length + count > 1024 * 1024) throw new InvalidDataException("Hook input is too large.");
                text.Append(buffer, 0, count);
            }
            var roots = Parse(provider, text.ToString()); timeout.Token.ThrowIfCancellationRequested();
            var catalog = new CatalogStore(paths, timeoutSeconds: 1, recoverInterruptedJobs: false);
            timeout.Token.ThrowIfCancellationRequested();
            if (roots.Count > 0) catalog.RegisterDiscovered(PathSafety.DisplayName(roots[0]), roots, provider);
            timeout.Token.ThrowIfCancellationRequested();
            catalog.SaveSetting("hook-last-registration:" + provider, DateTimeOffset.UtcNow);
            return true;
        }
        catch (Exception error)
        {
            // Native hooks must never block an agent with an error or return a permission/context decision.
            // Keep only a bounded error category; no payload, exception messages, or transcript paths.
            try
            {
                var log = Path.Combine(paths.DataDirectory, "hook-errors.log");
                if (!File.Exists(log) || new FileInfo(log).Length < 64 * 1024)
                    await File.AppendAllTextAsync(log, DateTimeOffset.UtcNow.ToString("O") + " " +
                        (DiscoveryProviders.All.Contains(provider) ? provider : "unknown") + " registration unavailable (" + error.GetType().Name + ")\n", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception) { }
            return false;
        }
    }
}
