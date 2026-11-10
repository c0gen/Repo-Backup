using RepoBackup.Core.Application;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Git;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class SourceSafetyRegressionTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        foreach (var protection in Enum.GetValues<DestinationProtection>())
            await tests.Run("Nested Git metadata, filtered roots and inherited Git environment recover safely: " + protection,
                () => RecoverAsync(Path.Combine(root, protection.ToString()), protection));
    }

    private static async Task RecoverAsync(string root, DestinationProtection protection)
    {
        var app = new ApplicationServices(Path.Combine(root, "catalog"), discoveryProviders: []);
        var git = new GitInspector(new ProcessRunner());
        var source = Path.Combine(root, "project"); Directory.CreateDirectory(source);
        var metadata = Path.Combine(source, "obj");
        async Task Git(params string[] args) => (await git.ReadAsync(source, args)).EnsureSuccess("Prepare nested metadata fixture");
        await Git("init", "--separate-git-dir", metadata);
        await Git("config", "user.name", "Source regression"); await Git("config", "user.email", "fixture@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(source, "keep.txt"), "committed content");
        await Git("add", "keep.txt"); await Git("-c", "commit.gpgsign=false", "commit", "-m", "fixture");
        await File.WriteAllTextAsync(Path.Combine(source, "keep.txt"), "unfinished content");
        var filtered = Path.Combine(root, "filtered root"); Directory.CreateDirectory(Path.Combine(filtered, "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(filtered, "node_modules", "excluded.txt"), "must never return");
        var empty = Path.Combine(root, "empty root"); Directory.CreateDirectory(empty);
        Directory.CreateDirectory(Path.Combine(source, "blocked"));
        await File.WriteAllTextAsync(Path.Combine(source, "blocked", "excluded.txt"), "must never return");
        var project = app.Catalog.RegisterCandidate("Source regression", [source, filtered, empty], "Fixture", approved: true);
        project = project with { Exclusions = new() { RelativePatterns = ["obj", "blocked/*"] } }; app.Catalog.SaveProject(project);
        foreach (var rules in new[] { new ExclusionRules(), new ExclusionRules { ExcludedDirectories = [], RelativePatterns = ["obj"] } })
        {
            var preview = await app.Planner.PreviewAsync(project with { Exclusions = rules });
            Assert(preview.Complete && preview.Files.Any(f => f.Path == Path.Combine(metadata, "HEAD")), "Nested Git database was excluded.");
        }
        var destination = await app.Backups.AddDestinationAsync("Fixture", Path.Combine(root, "repository"), protection: protection);
        var job = (await app.Backups.RunAsync(destination, [project])).Single(); Assert(RunOutcome.IsSuccessful(job), Json.Write(job));
        var snapshot = (await app.Restic.SnapshotsAsync(destination)).Single();
        var manifest = await app.Restic.ManifestAsync(destination, snapshot);
        Assert(manifest.SchemaVersion == 4 && manifest.GitMetadataDirectories.Contains(metadata), "Nested metadata role was discarded.");
        var filteredMapping = manifest.Sources.Single(s => s.OriginalPath == filtered);
        Assert(manifest.Directories.Contains(new(filteredMapping.Id, ".")), "Filtered root has no recovery representation.");
        var before = await Fixture.HashesAsync(source);
        var variables = new Dictionary<string, string?>
        {
            ["GIT_DIR"] = metadata, ["GIT_WORK_TREE"] = source, ["GIT_COMMON_DIR"] = metadata,
            ["GIT_INDEX_FILE"] = Path.Combine(metadata, "index"), ["GIT_OBJECT_DIRECTORY"] = Path.Combine(metadata, "objects"),
            ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = Path.Combine(metadata, "objects"),
            ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_KEY_0"] = "core.worktree", ["GIT_CONFIG_VALUE_0"] = source,
            ["GIT_CONFIG_PARAMETERS"] = "'core.worktree=" + source + "'", ["GIT_CONFIG_GLOBAL"] = Path.Combine(metadata, "config")
        };
        var savedEnvironment = variables.ToDictionary(p => p.Key, p => Environment.GetEnvironmentVariable(p.Key));
        var target = Path.Combine(root, "restored");
        try
        {
            foreach (var (key, value) in variables) Environment.SetEnvironmentVariable(key, value);
            await app.Restore.RestoreAsync(destination, snapshot, target);
        }
        finally { foreach (var (key, value) in savedEnvironment) Environment.SetEnvironmentVariable(key, value); }
        var after = await Fixture.HashesAsync(source);
        Assert(before.Count == after.Count && before.All(p => after.GetValueOrDefault(p.Key) == p.Value), "Recovery changed original Git files.");
        var restoredSource = Path.Combine(target, "sources", manifest.Sources.Single(s => s.OriginalPath == source).RestoreFolder);
        var resolved = await git.ReadAsync(restoredSource, ["rev-parse", "--show-toplevel"]); resolved.EnsureSuccess("Read recovered Git root");
        Assert(PathSafety.Normalize(resolved.Output.Trim()) == restoredSource && await File.ReadAllTextAsync(Path.Combine(restoredSource, "keep.txt")) == "unfinished content", "Recovery used the original Git repository.");
        Assert(Directory.Exists(Path.Combine(target, "sources", filteredMapping.RestoreFolder)) &&
            !Directory.Exists(Path.Combine(target, "sources", filteredMapping.RestoreFolder, "node_modules")), "Excluded contents were reintroduced.");
        Assert(Directory.Exists(Path.Combine(restoredSource, "blocked")) && !Directory.EnumerateFileSystemEntries(Path.Combine(restoredSource, "blocked")).Any(), "Filtered nested directory was not restored empty.");
        var selected = Path.Combine(root, "selected-empty");
        await app.Restore.RestoreAsync(destination, snapshot, selected, filteredMapping.SnapshotPath);
        Assert(Directory.Exists(Path.Combine(selected, "sources", filteredMapping.RestoreFolder)), "Selective empty-directory restore failed.");
        var fresh = new ApplicationServices(Path.Combine(root, "fresh-catalog"), discoveryProviders: []);
        var reopened = await fresh.Backups.AddDestinationAsync("Recovered", destination.Path,
            protection == DestinationProtection.RecoveryKey ? app.Credentials.Get(destination.Id) : null, true, protection: protection);
        Assert(await fresh.Restore.RebuildCatalogAsync(reopened) == 1, "Version 3 catalog reconstruction failed.");
        await fresh.Restore.RestoreAsync(reopened, snapshot, Path.Combine(root, "fresh-restore"));
        var only = Path.Combine(root, "only-filtered"); Directory.CreateDirectory(Path.Combine(only, "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(only, "node_modules", "skip.txt"), "skip");
        var emptyProject = app.Catalog.RegisterCandidate("Only filtered", [only], "Fixture", approved: true);
        var emptyJob = (await app.Backups.RunAsync(destination, [emptyProject])).Single();
        Assert(RunOutcome.IsSuccessful(emptyJob) && emptyJob.Files == 0, Json.Write(emptyJob));
        await app.Restore.RestoreAsync(destination, (await app.Restic.SnapshotsAsync(destination)).Single(s => s.TagValue("run") == emptyJob.Id), Path.Combine(root, "only-filtered-restored"));
    }
}
