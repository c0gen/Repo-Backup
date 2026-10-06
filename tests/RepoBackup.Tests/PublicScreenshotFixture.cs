using RepoBackup.Core.Application;
using RepoBackup.Core.Models;

namespace RepoBackup.Tests;

public static class PublicScreenshotFixture
{
    public const string PreviewProjectName = "Sample Audio Suite";

    public static async Task PrepareAsync(string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("The synthetic screenshot fixture needs an empty root.");
        Directory.CreateDirectory(root);
        var state = Path.Combine(root, "empty-codex-state.json");
        await File.WriteAllTextAsync(state, "{\"local-projects\":{}}");
        // Backups refresh only this empty fixture state, never installed project registries.
        var app = new ApplicationServices(Path.Combine(root, "catalog"), codexStatePath: state);
        var projects = Path.Combine(root, "Projects");
        var previewPath = Path.Combine(projects, PreviewProjectName);
        Directory.CreateDirectory(Path.Combine(previewPath, "assets"));
        await File.WriteAllTextAsync(Path.Combine(previewPath, "README.md"), "# Sample Audio Suite\n\nSynthetic screenshot fixture.\n");
        await File.WriteAllTextAsync(Path.Combine(previewPath, "assets", "waveform.txt"), "Demo asset\n");
        await File.WriteAllTextAsync(Path.Combine(previewPath, ".gitignore"), ".env\nnode_modules/\n");
        await File.WriteAllTextAsync(Path.Combine(previewPath, ".env"), "DEMO_SETTING=fixture-only\n");
        await File.WriteAllTextAsync(Path.Combine(previewPath, "notes.txt"), "Synthetic unfinished work\n");
        var previewProject = app.Catalog.RegisterCandidate(PreviewProjectName, [previewPath], "Fixture", approved: true);

        foreach (var name in new[] { "Atlas Notes", "Garden Planner", "Taskboard Demo" })
        {
            var path = Path.Combine(projects, name);
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "README.md"), $"# {name}\n\nSynthetic screenshot fixture.\n");
            app.Catalog.RegisterCandidate(name, [path], "Fixture", approved: true);
        }

        var archivePath = Path.Combine(projects, "Archive Sample");
        Directory.CreateDirectory(archivePath);
        var archive = app.Catalog.RegisterCandidate("Archive Sample", [archivePath], "Fixture", approved: true);
        app.Catalog.SaveProject(archive with { Enabled = false });

        var discoveryPath = Path.Combine(projects, "Sketchbook Idea");
        Directory.CreateDirectory(discoveryPath);
        app.Catalog.RegisterCandidate("Sketchbook Idea", [discoveryPath], "Fixture");

        var destination = await app.Backups.AddDestinationAsync("Demo Backup Drive", Path.Combine(root, "backup-repository"));
        var result = (await app.Backups.RunAsync(destination, [previewProject])).Single();
        if (result.Backup != Outcome.Successful || result.Verification != Outcome.Successful)
            throw new InvalidOperationException("Synthetic screenshot backup did not complete successfully.");
        var demoLocalTime = new DateTime(2026, 1, 15, 10, 30, 0);
        var demoTime = new DateTimeOffset(demoLocalTime, TimeZoneInfo.Local.GetUtcOffset(demoLocalTime));
        app.Catalog.SaveJob(result with { StartedAt = demoTime.AddMinutes(-1), FinishedAt = demoTime });

        Console.WriteLine("Synthetic fixture catalog: " + app.Paths.DataDirectory);
    }
}
