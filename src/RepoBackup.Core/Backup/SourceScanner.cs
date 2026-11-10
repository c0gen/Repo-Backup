using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

internal static class SourceScanner
{
    // Enumeration fills FileSystemInfo's metadata cache in one directory read, avoiding
    // separate attribute/size/timestamp calls for every ordinary child.
    public static IEnumerable<FileSystemInfo> Children(string path) => new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions
    {
        AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false
    });

    public static FileSystemInfo Root(string path) => (File.GetAttributes(path) & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);

    public static IEnumerable<string> ReadabilityWarnings(IEnumerable<FileEntry> files, CancellationToken token,
        IProgress<SourceScanProgress>? progress = null) => ReadabilityIssues(files, token, progress).Select(i => i.Message);

    public static IEnumerable<BackupIssue> ReadabilityIssues(IEnumerable<FileEntry> files, CancellationToken token,
        IProgress<SourceScanProgress>? progress = null)
    {
        var reporter = new SourceScanReporter(progress);
        long count = 0, bytes = 0;
        var lastPath = "";
        foreach (var file in files.Where(f => !f.IsDirectory && !f.IsLink))
        {
            token.ThrowIfCancellationRequested();
            lastPath = file.Path;
            reporter.Report(file.Path, ++count, bytes += file.Length);
            BackupIssue? issue = null;
            try
            {
                // No content is read. Restic may reuse unchanged data without opening
                // the file; require read access so that live coverage still reports locks/ACLs.
                using var handle = File.OpenHandle(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { issue = SourceAvailability.Issue(file.Path, e); }
            if (issue is not null) yield return issue;
        }
        reporter.Report(lastPath, count, bytes, force: true);
    }
}
