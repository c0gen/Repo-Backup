using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

public static class SourceAvailability
{
    public static BackupIssue Issue(string path, Exception error, bool root = false)
    {
        var code = error is UnauthorizedAccessException ? "source.access-denied" : "source.unavailable";
        if (error is FileNotFoundException or DirectoryNotFoundException)
        {
            try { _ = File.GetAttributes(Path.GetPathRoot(path)!); code = "source.missing"; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { code = "source.drive-unavailable"; }
        }
        var label = code switch { "source.access-denied" => "Access denied", "source.missing" => "Source folder or file is missing",
            "source.drive-unavailable" => "Source drive or share is unavailable", _ => "Source unavailable" };
        return new(code, BackupIssueSeverity.CoverageError, path, label + ": " + path + " — " + error.Message
            + (root ? " Use Relink source folder to locate a moved folder." : ""));
    }
}
