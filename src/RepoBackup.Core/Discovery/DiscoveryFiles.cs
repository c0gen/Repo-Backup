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

    public static async Task<string> ReadFirstLineAsync(string path, int maxBytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var header = new MemoryStream(); var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes - (int)header.Length + 1)), token).ConfigureAwait(false);
            if (count == 0) break;
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
            var length = newline < 0 ? count : newline;
            if (header.Length + length > maxBytes) throw new InvalidDataException("Discovery session header exceeds the supported size.");
            header.Write(buffer, 0, length);
            if (newline >= 0) break;
        }
        // Decode and parse only the first record, even for a large transcript.
        return new UTF8Encoding(false, true).GetString(header.GetBuffer(), 0, (int)header.Length).TrimStart('\uFEFF').TrimEnd('\r');
    }

    public static bool IsMissingOnAvailableVolume(Exception error, string reference)
    {
        if (error is not (FileNotFoundException or DirectoryNotFoundException)) return false;
        try
        {
            var path = WorkspaceFileReader.LocalPath(reference);
            if (path is null) return false;
            // Probe the volume/share without File.Exists, which hides access errors.
            File.GetAttributes(Path.GetPathRoot(path)!);
            return true;
        }
        catch (Exception e) when (IsReadError(e)) { return false; }
    }

    // Parser exceptions can contain untrusted source text. Never put their messages in the catalog or UI.
    public static string Warning(string source, Exception e) => source + (e is JsonException or InvalidDataException or ArgumentException or InvalidOperationException
        ? ": saved project data is invalid or unsupported; existing projects were preserved."
        : ": saved project data could not be read; existing projects were preserved.");

    public static string PathId(string normalizedPath) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath.ToUpperInvariant())));
}
