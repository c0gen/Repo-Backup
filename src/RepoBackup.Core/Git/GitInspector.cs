using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Git;

public sealed partial class GitInspector(ProcessRunner processes)
{
    private static Dictionary<string, string?> CreateReadEnvironment()
    {
        var environment = new Dictionary<string, string?> { ["GIT_OPTIONAL_LOCKS"] = "0", ["GIT_TERMINAL_PROMPT"] = "0" };
        // These variables override -C or inject configuration from a calling Git process.
        foreach (var name in new[] { "GIT_DIR", "GIT_COMMON_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY",
            "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG", "GIT_CONFIG_COUNT", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_GLOBAL",
            "GIT_CONFIG_SYSTEM", "GIT_NAMESPACE", "GIT_PREFIX", "GIT_SHALLOW_FILE", "GIT_CEILING_DIRECTORIES", "GIT_DISCOVERY_ACROSS_FILESYSTEM" })
            environment[name] = null;
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(n =>
            n.StartsWith("GIT_CONFIG_KEY_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase)))
            environment[name] = null;
        if (Path.GetDirectoryName(BundledTools.GitExecutable) is { Length: > 0 } directory)
            environment["PATH"] = directory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        return environment;
    }

    public Task<ProcessResult> ReadAsync(string root, IEnumerable<string> args, CancellationToken token = default) =>
        ExecuteAsync(new[] { "--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.longpaths=true", "-C", NativePath(root) }.Concat(args), token);

    public Task<ProcessResult> ReadAsync(GitContext context, IEnumerable<string> args, CancellationToken token = default) =>
        ExecuteAsync(new[] { "--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.longpaths=true", "--git-dir", NativePath(context.GitDirectory),
            "--work-tree", NativePath(context.WorkingPath), "-C", NativePath(context.WorkingPath) }.Concat(args), token);

    private static string NativePath(string path) => path.Length >= 260 ? PathSafety.ExtendedPath(path) : path;

    private Task<ProcessResult> ExecuteAsync(IEnumerable<string> args, CancellationToken token)
    {
        var arguments = args.ToArray();
        return arguments.Contains("-z")
            ? processes.RunRawAsync(BundledTools.GitExecutable, arguments, token, CreateReadEnvironment())
            : processes.RunAsync(BundledTools.GitExecutable, arguments, token, environment: CreateReadEnvironment());
    }

    public Task<List<(string Path, SourceKind Kind)>> AssociatedSourcesAsync(string root, List<string> warnings, CancellationToken token) =>
        AssociatedSourcesAsync(new[] { root }, warnings, token);

    public async Task<List<(string Path, SourceKind Kind)>> AssociatedSourcesAsync(IEnumerable<string> roots, List<string> warnings, CancellationToken token)
    {
        var inventory = await InventoryAsync(roots, token);
        warnings.AddRange(inventory.Issues.Where(i => i.Severity == BackupIssueSeverity.CoverageError).Select(i => i.Message));
        return inventory.Sources;
    }

    public async Task<HashSet<string>> TrackedFilesAsync(string root, CancellationToken token)
    {
        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git"))) return new(StringComparer.OrdinalIgnoreCase);
        var result = await ReadAsync(root, ["ls-files", "--cached", "--recurse-submodules", "-z"], token);
        result.EnsureSuccess("Read tracked Git files");
        return result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(p => Path.GetFullPath(p, root)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
