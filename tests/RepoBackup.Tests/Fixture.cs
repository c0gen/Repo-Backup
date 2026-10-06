using System.Security.Cryptography;
using RepoBackup.Core.Application;
using RepoBackup.Core.Git;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Tests;

public sealed class Fixture
{
    public string Root { get; }
    public string Main => Path.Combine(Root, "sources", "Spectrum Suite");
    public string Worktree => Path.Combine(Root, "sources", "audio-fixes");
    public string OtherWorktree => Path.Combine(Root, "sources", "cuda-dsp");
    public string State => Path.Combine(Root, "codex-state.json");
    public ApplicationServices App { get; }
    public ProjectEntry Project { get; private set; } = null!;
    public Destination Destination { get; set; } = null!;
    public SnapshotInfo Snapshot { get; set; } = null!;
    public Dictionary<string, string> OriginalHashes { get; private set; } = [];
    private readonly ProcessRunner processes = new();
    public Fixture(string root)
    {
        Root = root; Directory.CreateDirectory(root); File.WriteAllText(State, "{\"local-projects\":{}}");
        App = new(Path.Combine(root, "catalog"), codexStatePath: State);
    }
    public async Task Git(string directory, params string[] args)
    {
        (await processes.RunAsync("git", new[] { "-C", directory }.Concat(args))).EnsureSuccess("Git fixture setup");
    }
    public async Task<string> GitOutput(string directory, params string[] args)
    {
        var result = await new GitInspector(processes).ReadAsync(directory, args); result.EnsureSuccess("Read fixture Git"); return result.Output;
    }
    public async Task PrepareAsync()
    {
        Directory.CreateDirectory(Main);
        await Git(Main, "init", "-b", "main"); await Git(Main, "config", "user.name", "Backup Test"); await Git(Main, "config", "user.email", "backup-test@example.invalid");
        Directory.CreateDirectory(Path.Combine(Main, "node_modules")); Directory.CreateDirectory(Path.Combine(Main, "build")); Directory.CreateDirectory(Path.Combine(Main, "dist")); Directory.CreateDirectory(Path.Combine(Main, "assets"));
        await File.WriteAllTextAsync(Path.Combine(Main, ".gitignore"), ".env\nnode_modules/cache.txt\n");
        await File.WriteAllTextAsync(Path.Combine(Main, "README.md"), "Original commit\n");
        await File.WriteAllTextAsync(Path.Combine(Main, "staged.txt"), "committed\n");
        await File.WriteAllTextAsync(Path.Combine(Main, "node_modules", "tracked.txt"), "This tracked dependency must be preserved.\n");
        await File.WriteAllTextAsync(Path.Combine(Main, "build", "keep.txt"), "Build output included by default\n");
        await File.WriteAllTextAsync(Path.Combine(Main, "dist", "keep.txt"), "Distribution asset included by default\n");
        await File.WriteAllTextAsync(Path.Combine(Main, "assets", "texture.txt"), "Texture data\n");
        await Git(Main, "add", "."); await Git(Main, "commit", "-m", "Initial fixture");
        var library = Path.Combine(Root, "library"); Directory.CreateDirectory(library); await Git(library, "init"); await Git(library, "config", "user.name", "Backup Test"); await Git(library, "config", "user.email", "backup-test@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(library, "module.txt"), "Local submodule contents\n"); await Git(library, "add", "."); await Git(library, "commit", "-m", "Library fixture");
        await Git(Main, "-c", "protocol.file.allow=always", "submodule", "add", library, "module"); await Git(Main, "commit", "-am", "Add local submodule");
        await Git(Main, "worktree", "add", "-b", "audio-fixes", Worktree); await Git(Main, "worktree", "add", "-b", "cuda-dsp", OtherWorktree);
        await File.WriteAllTextAsync(Path.Combine(Main, "staged.txt"), "staged version\n"); await Git(Main, "add", "staged.txt");
        await File.WriteAllTextAsync(Path.Combine(Main, "staged.txt"), "unstaged version\n");
        await File.WriteAllTextAsync(Path.Combine(Main, ".env"), "TEST_SETTING=fixture-only\n");
        await File.WriteAllTextAsync(Path.Combine(Main, "untracked.txt"), "Unfinished work\n");
        await File.WriteAllTextAsync(Path.Combine(Main, "node_modules", "cache.txt"), "Excluded cached dependency\n");
        await File.WriteAllTextAsync(Path.Combine(Worktree, "unfinished.txt"), "Worktree untracked changes\n");
        await File.WriteAllTextAsync(Path.Combine(OtherWorktree, ".env"), "WORKTREE_CONFIG=fixture-only\n");
        Project = App.Catalog.RegisterCandidate("RFNM Spectrum Suite", [Main], "Fixture", approved: true);
        OriginalHashes = await HashesAsync(Main);
    }
    public static async Task<string> HashAsync(string file)
    { await using var stream = File.OpenRead(file); return Convert.ToHexString(await SHA256.HashDataAsync(stream)); }
    public static async Task<Dictionary<string, string>> HashesAsync(string directory)
    {
        var result = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) result[Path.GetRelativePath(directory, file)] = await HashAsync(file);
        return result;
    }
}
