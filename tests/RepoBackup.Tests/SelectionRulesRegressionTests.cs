using RepoBackup.Cli;
using RepoBackup.Core.Application;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class SelectionRulesRegressionTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Inherited exclusions keep project meaning across selections and relinking", async () =>
        {
            var app = new ApplicationServices(Path.Combine(root, "catalog"), discoveryProviders: []);
            var first = Path.Combine(root, "first"); var second = Path.Combine(root, "second");
            foreach (var path in new[] { first, second })
            {
                Directory.CreateDirectory(Path.Combine(path, "assets"));
                await File.WriteAllTextAsync(Path.Combine(path, "assets", "private.txt"), "excluded");
                await File.WriteAllTextAsync(Path.Combine(path, "assets", "keep.txt"), "retained");
            }
            var project = app.Catalog.RegisterCandidate("Rules", [first, second], "Fixture", approved: true);
            project = project with { Exclusions = new() { RelativePatterns = ["assets/private.txt"] } }; app.Catalog.SaveProject(project);
            var selection = SavedSelection.FromProject(project, "Assets", [Path.Combine(first, "assets"), Path.Combine(second, "assets")]);
            app.Catalog.SaveSelection(selection);
            foreach (var preview in new[] { await app.Planner.PreviewAsync(project), await app.Planner.PreviewAsync(project, selection) })
                Assert(preview.Files.Count == 2 && preview.Files.All(f => Path.GetFileName(f.Path) == "keep.txt"), "Inherited project pattern changed meaning.");
            var old = Json.Read<SavedSelection>("{\"projectId\":\"" + project.Id + "\",\"name\":\"Legacy\",\"paths\":[" + Json.Write(Path.Combine(first, "assets")) + "],\"exclusions\":{\"relativePatterns\":[\"private.txt\"]}}");
            var legacyPreview = await app.Planner.PreviewAsync(project, old);
            Assert(old.PatternBase == ExclusionPatternBase.SelectedSource && legacyPreview.Files.Count == 1, "Legacy selection patterns were reinterpreted.");
            var moved = Path.Combine(root, "moved");
            Assert(PathSafety.IsWithin(first, root) && PathSafety.IsWithin(moved, root), "Unsafe fixture move."); Directory.Move(first, moved);
            app.Catalog.RelinkRoot(project.Id, project.Roots[0].Id, moved);
            var relinked = await app.Planner.PreviewAsync(app.Catalog.Projects().Single(), app.Catalog.Selections().Single());
            Assert(relinked.Files.Count == 2 && relinked.Files.All(f => Path.GetFileName(f.Path) == "keep.txt"), "Relinking changed the exclusion base.");
            using var output = new StringWriter(); using var errors = new StringWriter();
            var code = await CommandHost.RunAsync(["--data-dir", app.Paths.DataDirectory, "selection-save", "--project", project.Id, "--name", "CLI", "--paths", Path.Combine(moved, "assets")], output, errors);
            Assert(code == 0 && Json.Read<SavedSelection>(output.ToString()).PatternBase == ExclusionPatternBase.ProjectRoot, errors.ToString());
        });
    }
}
