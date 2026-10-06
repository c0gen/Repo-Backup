using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Storage;
using RepoBackup.Core.Windows;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class CatalogTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        var paths = new AppPaths(Path.Combine(root, "unit-catalog")); var catalog = new CatalogStore(paths);
        var source = Path.Combine(root, "first-source"); Directory.CreateDirectory(source);
        var state = Path.Combine(root, "codex.json");
        await File.WriteAllTextAsync(state, Json.Write(new Dictionary<string, object> { ["local-projects"] = new Dictionary<string, object> { ["local-test"] = new { name = "Multiple drives", rootPaths = new[] { source, "F:\\missing-" + Guid.NewGuid().ToString("N") } } } }));
        var discovery = new ProjectDiscoveryService(catalog, [new CodexDiscovery(state)]);
        await tests.Run("Discovery preserves multiple roots and remains a review candidate", async () => { var result = await discovery.RefreshAsync(); Assert(result.Projects == 1 && result.Roots == 2, "Incorrect root count."); Assert(!catalog.Projects()[0].Enabled && !catalog.Projects()[0].Reviewed, "Discovery enrolled a project."); });
        await tests.Run("Repeated discovery does not duplicate or reset approval", async () => { var project = catalog.Projects()[0]; catalog.SaveProject(project with { Reviewed = true, Enabled = true }); await discovery.RefreshAsync(); await discovery.RefreshAsync(); Assert(catalog.Projects().Count == 1 && catalog.Projects()[0].Enabled, "Approval or identity lost."); });
        await tests.Run("Relinking preserves roots, selections and ignores stale Codex paths", async () =>
        {
            var project = catalog.Projects()[0]; var selection = new SavedSelection { ProjectId = project.Id, Name = "Assets", Paths = [Path.Combine(source, "assets")] }; catalog.SaveSelection(selection);
            var moved = Path.Combine(root, "moved-source"); Directory.CreateDirectory(moved); catalog.RelinkRoot(project.Id, project.Roots[0].Id, moved); await discovery.RefreshAsync();
            var updated = catalog.Projects()[0]; Assert(updated.Id == project.Id && updated.Roots.Count == 2 && updated.Roots[0].Path == moved, "Relink was undone or duplicated."); Assert(catalog.Selections()[0].Paths[0] == Path.Combine(moved, "assets"), "Selection not relinked.");
        });
        await tests.Run("Changed or corrupt Codex state leaves the catalog intact", async () => { var before = catalog.Export(); await File.WriteAllTextAsync(state, "{\"unknown-project-format\":42}"); var result = await discovery.RefreshAsync(); Assert(result.Warnings.Count == 1 && catalog.Export() == before, "Bad discovery changed catalog."); await File.WriteAllTextAsync(state, "{"); Assert((await discovery.RefreshAsync()).Warnings.Count == 1, "Parse error not reported."); });
        await tests.Run("Concurrent catalog instances register one stable candidate", async () => { var other = new CatalogStore(paths); var candidate = Path.Combine(root, "concurrent-source"); await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => (i % 2 == 0 ? catalog : other).RegisterCandidate("Concurrent", [candidate], "Hook", "concurrent")))); Assert(catalog.Projects().Count(p => p.ExternalId == "concurrent") == 1, "Concurrent discovery duplicated identity."); });
        await tests.Run("JSON catalog roundtrip is portable and never exports credentials", () => { var exported = catalog.Export(); var copy = new CatalogStore(new AppPaths(Path.Combine(root, "import-catalog"))); copy.Import(exported); Assert(copy.Projects().Count == catalog.Projects().Count && copy.Selections().Count == 1, "Import incomplete."); Assert(!exported.Contains("password", StringComparison.OrdinalIgnoreCase), "Credentials in export."); return Task.CompletedTask; });
        await tests.Run("Unsupported import is rejected before changing the catalog", async () => { var before = catalog.Export(); await Throws<InvalidDataException>(() => { catalog.Import(before.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 99")); return Task.CompletedTask; }); Assert(before == catalog.Export(), "Invalid import changed the catalog."); });
        await tests.Run("An import database constraint failure rolls back all earlier writes", async () =>
        {
            var before = catalog.Export();
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath }.ToString()); db.Open();
            using var command = db.CreateCommand(); command.CommandText = "CREATE TRIGGER reject_import BEFORE INSERT ON destinations WHEN NEW.id='reject-fixture' BEGIN SELECT RAISE(ABORT,'fixture constraint'); END"; command.ExecuteNonQuery();
            var incoming = new CatalogExport(1, [new ProjectEntry { Name = "Would be inserted", Roots = [new(Guid.NewGuid().ToString("N"), Path.Combine(root, "first-import-root"))] }], [],
                [new Destination { Id = "reject-fixture", Name = "Rejected destination", Path = Path.Combine(root, "rejected-destination") }], [], []);
            try { await Throws<SqliteException>(() => { catalog.Import(Json.Write(incoming)); return Task.CompletedTask; }); Assert(catalog.Export() == before, "Import committed partial changes."); }
            finally { command.CommandText = "DROP TRIGGER reject_import"; command.ExecuteNonQuery(); }
        });
        await tests.Run("Interrupted jobs are recognized without disturbing a live process", () =>
        {
            var project = catalog.Projects()[0]; var dead = new JobRecord { ProjectId = project.Id, ProjectName = project.Name, DestinationId = "none", SeriesId = project.Id, OwnerProcessId = int.MaxValue, OwnerStartTicks = 1 };
            var live = new JobRecord { ProjectId = project.Id, ProjectName = project.Name, DestinationId = "none", SeriesId = project.Id }; catalog.SaveJob(dead); catalog.SaveJob(live); var reopened = new CatalogStore(paths);
            Assert(reopened.History().Single(j => j.Id == dead.Id).Backup == Outcome.Interrupted, "Crash not recorded."); Assert(reopened.History().Single(j => j.Id == live.Id).Backup == Outcome.Running, "Active operation was incorrectly interrupted."); return Task.CompletedTask;
        });
        await tests.Run("Destination paths reject recursion and correctly handle drive roots", async () => { Assert(PathSafety.IsWithin("C:\\project", "C:\\"), "Drive root containment incorrect."); Assert(!PathSafety.IsWithin("C:\\projects-other", "C:\\projects"), "Sibling containment incorrect."); await Throws<InvalidOperationException>(() => { PathSafety.EnsureDestinationOutsideSources(Path.Combine(source, "backups"), [source]); return Task.CompletedTask; }); Assert(PathSafety.IsBroad("F:\\") && PathSafety.IsBroad("C:\\Users\\Tester\\Downloads"), "Broad source warning missing."); });
        await tests.Run("DPAPI keys roundtrip and remain separate from catalog exports", () => { var credentials = new CredentialStore(paths); var id = Guid.NewGuid().ToString("N"); var key = CredentialStore.Generate(); credentials.Save(id, key); Assert(credentials.Get(id) == key, "Key did not decrypt."); Assert(!catalog.Export().Contains(key), "Export includes recovery key."); credentials.Delete(id); return Task.CompletedTask; });
        await tests.Run("Hook merging preserves existing handlers and is idempotent", () => { var existing = "{\"hooks\":{\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"existing-command\"}]}]}}"; var merged = CodexHookInstaller.Merge(existing, "C:\\App\\RepoBackup.Runner.exe"); Assert(merged.Contains("existing-command") && merged.Contains("\"async\": true"), "Hook lost existing definition or async mode."); Assert(merged == CodexHookInstaller.Merge(merged, "C:\\App\\RepoBackup.Runner.exe"), "Hook merge duplicated handler."); Assert(!merged.Contains("backup --"), "Hook starts backups."); return Task.CompletedTask; });
        await tests.Run("Schedules are disabled by default and use hidden non-overlapping catch-up", () =>
        {
            Assert(catalog.Schedules().Count == 0, "Scheduling initialized enabled.");
            foreach (var kind in Enum.GetValues<ScheduleKind>())
            {
                var schedule = new ScheduleDefinition { DestinationId = "fixture", Kind = kind }; var xml = XDocument.Parse(WindowsScheduler.BuildXml(schedule, "C:\\App\\RepoBackup.Runner.exe", paths.DataDirectory, "S-1-5-21-test")); XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                Assert(xml.Descendants(ns + "MultipleInstancesPolicy").Single().Value == "IgnoreNew" && xml.Descendants(ns + "StartWhenAvailable").Single().Value == "true", "Scheduler concurrency or catch-up missing."); Assert(xml.Descendants(ns + "Hidden").Single().Value == "true" && xml.Descendants(ns + "LogonType").Single().Value == "InteractiveToken", "Scheduler user or hidden mode incorrect."); Assert(xml.Root!.Element(ns + "Settings")!.Element(ns + "Enabled")!.Value == "false", "Schedule started enabled.");
            }
            return Task.CompletedTask;
        });
    }
}
