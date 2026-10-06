using System.Text;

namespace RepoBackup.Core.Infrastructure;

public static class WindowsCommandLine
{
    // Windows native process arguments use backslashes to escape quotes, including
    // a final backslash immediately before a closing quote (for example C:\).
    public static string Join(IEnumerable<string> arguments) => string.Join(" ", arguments.Select(Quote));

    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes); slashes = 0;
            result.Append(character);
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
