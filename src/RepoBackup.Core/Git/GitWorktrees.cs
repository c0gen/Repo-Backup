using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Git;

public sealed partial class GitInspector
{
    public static List<GitWorktreeRecord> ParseWorktrees(string output)
    {
        var result = new List<GitWorktreeRecord>();
        string? path = null, head = null, branch = null, prunable = null; var locked = false;
        void Flush()
        {
            if (path is not null) result.Add(new(path, head, branch, locked, prunable));
            path = head = branch = prunable = null; locked = false;
        }
        foreach (var field in output.Split('\0'))
        {
            if (field.Length == 0) { Flush(); continue; }
            if (field.StartsWith("worktree ", StringComparison.Ordinal)) { Flush(); path = field[9..]; }
            else if (field.StartsWith("HEAD ", StringComparison.Ordinal)) head = field[5..];
            else if (field.StartsWith("branch ", StringComparison.Ordinal)) branch = field[7..];
            else if (field == "locked" || field.StartsWith("locked ", StringComparison.Ordinal)) locked = true;
            else if (field == "prunable" || field.StartsWith("prunable ", StringComparison.Ordinal)) prunable = field.Length > 9 ? field[9..] : "Stale registration";
        }
        Flush(); return result;
    }

    private async Task<GitContext> ContextAsync(string root, string? expectedCommon, CancellationToken token)
    {
        var result = await ReadAsync(root, ["rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir"], token);
        if (result.ExitCode != 0 && expectedCommon is not null)
        {
            var pointer = Path.Combine(root, ".git");
            if (!File.Exists(pointer)) result.EnsureSuccess("Read Git metadata");
            var contents = (await File.ReadAllTextAsync(pointer, token)).Trim();
            if (!contents.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid Git pointer: " + pointer);
            var old = contents[7..].Trim();
            if (!Path.IsPathFullyQualified(old)) throw new InvalidDataException("Cannot unambiguously resolve moved Git metadata: " + pointer);
            var oldDirectory = PathSafety.Normalize(old);
            try { _ = File.GetAttributes(oldDirectory); result.EnsureSuccess("Read existing Git metadata"); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                if (!VolumeAvailable(oldDirectory))
                    throw new IOException("Original Git metadata drive or share is unavailable; relocation cannot be confirmed: " + oldDirectory, e);
            }
            var id = Path.GetFileName(oldDirectory);
            var admin = Path.Combine(expectedCommon, "worktrees", id);
            if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(oldDirectory)), "worktrees", StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(admin)) throw new InvalidDataException("No matching moved worktree registration: " + pointer);
            var matchingRegistrations = 0;
            foreach (var candidate in Directory.EnumerateDirectories(Path.Combine(expectedCommon, "worktrees")))
            {
                var backFile = Path.Combine(candidate, "gitdir"); if (!File.Exists(backFile)) continue;
                var value = (await File.ReadAllTextAsync(backFile, token)).Trim();
                if (Path.IsPathFullyQualified(value) && PathSafety.Normalize(value).Equals(PathSafety.Normalize(pointer), StringComparison.OrdinalIgnoreCase)) matchingRegistrations++;
            }
            if (matchingRegistrations != 1) throw new InvalidDataException("Moved worktree has ambiguous administrative registrations: " + pointer);
            var back = (await File.ReadAllTextAsync(Path.Combine(admin, "gitdir"), token)).Trim();
            var common = Path.GetFullPath((await File.ReadAllTextAsync(Path.Combine(admin, "commondir"), token)).Trim(), admin);
            if (!Path.IsPathFullyQualified(back) || !PathSafety.PhysicalPath(back).Equals(PathSafety.PhysicalPath(pointer), StringComparison.OrdinalIgnoreCase)
                || !PathSafety.PhysicalPath(common).Equals(PathSafety.PhysicalPath(expectedCommon), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Moved worktree registration does not match its working directory: " + pointer);
            var context = new GitContext(PathSafety.Normalize(root), PathSafety.Normalize(admin), PathSafety.Normalize(expectedCommon));
            var check = await ReadAsync(context, ["rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir"], token);
            check.EnsureSuccess("Validate moved worktree metadata");
            result = check;
        }
        result.EnsureSuccess("Read Git metadata");
        var directories = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(PathSafety.Normalize).ToArray();
        if (directories.Length != 2 || directories.Any(d => !Directory.Exists(d))) throw new InvalidDataException("Git metadata is unavailable: " + root);
        var objects = Path.Combine(directories[1], "objects");
        if ((File.GetAttributes(objects) & FileAttributes.Directory) == 0)
            throw new InvalidDataException("Git object database is unavailable: " + objects);
        if (expectedCommon is not null && !PathSafety.PhysicalPath(directories[1]).Equals(PathSafety.PhysicalPath(expectedCommon), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Worktree points to a different common repository: " + root);
        return new(PathSafety.Normalize(root), directories[0], directories[1]);
    }

    private static bool VolumeAvailable(string path)
    {
        try { _ = File.GetAttributes(Path.GetPathRoot(path)!); return true; }
        catch (Exception e) when (IsInventoryError(e)) { return false; }
    }

    private static bool IsInventoryError(Exception e) => e is IOException or UnauthorizedAccessException or ArgumentException
        or InvalidDataException or System.ComponentModel.Win32Exception or NotSupportedException;
}
