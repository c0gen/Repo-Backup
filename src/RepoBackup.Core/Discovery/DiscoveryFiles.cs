using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RepoBackup.Core.Discovery;

internal static class DiscoveryFiles
{
    public static async Task<string> ReadAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 32 * 1024 * 1024) throw new InvalidDataException("Discovery metadata exceeds the supported size.");
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync(token).ConfigureAwait(false);
        if (text.Length > 32 * 1024 * 1024) throw new InvalidDataException("Discovery metadata exceeds the supported size.");
        return text;
    }

    public static bool IsReadError(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
        or ArgumentException or InvalidOperationException or System.Security.SecurityException;

    // Parser exceptions can contain untrusted source text. Never put their messages in the catalog or UI.
    public static string Warning(string source, Exception e) => source + (e is JsonException or InvalidDataException or ArgumentException or InvalidOperationException
        ? ": saved project data is invalid or unsupported; existing projects were preserved."
        : ": saved project data could not be read; existing projects were preserved.");

    public static string PathId(string normalizedPath) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath.ToUpperInvariant())));
}
