using System.Text;
using RepoBackup.Core.Infrastructure;

namespace RepoBackup.Core.Windows;

public static class HookConfigurationWriter
{
    public static bool Update(string path, Func<string?, string> merge)
    {
        path = PathSafety.Normalize(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var lease = new FileStream(path + ".repobackup.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        var merged = merge(existing); // Validate before backing up or replacing the user's file.
        if (existing == merged) return false;
        if (existing is not null) File.Copy(path, path + ".repobackup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff") + ".bak");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, merged, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }
}
