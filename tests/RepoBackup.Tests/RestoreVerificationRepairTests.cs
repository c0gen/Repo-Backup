using System.Text.Json;
using RepoBackup.Core.Application;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Git;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Recovery;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class RestoreVerificationRepairTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Windows path collisions fail before extraction while individual recovery remains possible", async () =>
        {
            var source = new SourceMapping("source", "C:\\source", "/C/source", "source", SourceKind.Project);
            SnapshotFile[] nodes = [new(source.SnapshotPath, "dir", 0), new(source.SnapshotPath + "/A.txt", "file", 1), new(source.SnapshotPath + "/a.txt", "file", 2)];
            await Throws<InvalidDataException>(() => Task.Run(() => RestorePaths.Plan(source, nodes)));
            Assert(RestorePaths.Plan(source, nodes, source.SnapshotPath + "/A.txt").Expected.Count == 1, "A colliding file cannot be recovered separately.");
        });
        foreach (var protection in new[] { DestinationProtection.PasswordFree, DestinationProtection.RecoveryKey })
        {
            var folder = Path.Combine(root, protection.ToString()); Directory.CreateDirectory(folder);
            var state = Path.Combine(folder, "state.json"); await File.WriteAllTextAsync(state, "{\"local-projects\":{}}");
            var app = new ApplicationServices(Path.Combine(folder, "catalog"), codexStatePath: state);
            var source = Path.Combine(folder, "source"); Directory.CreateDirectory(source);
            const string fileName = "payload[1].txt";
            var payload = Path.Combine(source, fileName); await File.WriteAllTextAsync(payload, "Captured original bytes\n");
            await File.WriteAllTextAsync(Path.Combine(source, "payload1.txt"), "A wildcard must never select this sibling\n");
            var outside = Path.Combine(folder, "original-outside-recovery.txt"); await File.WriteAllTextAsync(outside, "Other original bytes that must remain unchanged\n");
            var originalHash = await Fixture.HashAsync(payload); var outsideHash = await Fixture.HashAsync(outside);
            var project = app.Catalog.RegisterCandidate("Verification repair", [source], "Fixture", approved: true);
            var destination = await app.Backups.AddDestinationAsync("Verification repository", Path.Combine(folder, "repository"), protection: protection);
            var job = (await app.Backups.RunAsync(destination, [project])).Single(); Assert(job.Backup == Outcome.Successful, Json.Write(job));
            var snapshot = (await app.Restic.SnapshotSummariesAsync(destination)).Single();
            var selected = PathSafety.SnapshotPath(payload);
            foreach (var mode in new[] { "size", "content", "persistent", "outside", "hardlink" })
                await tests.Run($"{protection}: verified recovery retry handles {mode} without original writes or false success", async () =>
                {
                    var process = new ChangedOutputRunner(mode, fileName, payload, outside);
                    var client = new ResticClient(app.Paths, app.Credentials, process, app.Restic.Executable);
                    var service = new RestoreService(app.Paths, app.Catalog, client, new GitRecovery(new GitInspector(new ProcessRunner())));
                    var target = Path.Combine(folder, "restored-" + mode); var progress = new ProgressRecorder();
                    var before = app.Catalog.GetSetting<JsonElement>("lastRestore").ToString();
                    if (mode is "persistent" or "outside")
                    {
                        await Throws<IOException>(() => service.RestoreAsync(destination, snapshot, target, selected, progress));
                        Assert(!File.Exists(Path.Combine(target, "RESTORED.txt")) && File.Exists(Path.Combine(target, "RESTORE-INCOMPLETE.txt")) &&
                            progress.Items.All(p => p.Fraction < 1) && app.Catalog.GetSetting<JsonElement>("lastRestore").ToString() == before, "A failed repair claimed success.");
                        Assert(process.Restores == (mode == "outside" ? 1 : 2), "Retry was not bounded or wrote an unrelated path.");
                    }
                    else
                    {
                        await service.RestoreAsync(destination, snapshot, target, selected, progress);
                        var manifest = await app.Restic.ManifestAsync(destination, snapshot);
                        var restored = Path.Combine(target, "sources", manifest.Sources.Single(s => s.OriginalPath == source).RestoreFolder, fileName);
                        Assert(await Fixture.HashAsync(restored) == originalHash && process.Restores == 2, "Verified snapshot bytes were not recovered with one retry.");
                        Assert(File.Exists(Path.Combine(target, "restore-verification-repairs.json")) &&
                            !File.Exists(Path.Combine(Path.GetDirectoryName(restored)!, "payload1.txt")), "Repair provenance was lost or literal selection widened.");
                        progress.AssertSuccessfulRestore();
                    }
                    Assert(await Fixture.HashAsync(payload) == originalHash && await Fixture.HashAsync(outside) == outsideHash, "Repair modified original files through a hard link or reported path.");
                    Assert(!Directory.EnumerateFileSystemEntries(target, ".verification-repair-*").Any(), "Private retry data was not cleaned up.");
                });
        }
    }

    private sealed class ChangedOutputRunner(string mode, string name, string original, string outside) : ProcessRunner
    {
        public int Restores { get; private set; }
        public override async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token = default,
            string? workingDirectory = null, IReadOnlyDictionary<string, string?>? environment = null, Action<string>? onOutput = null,
            string? secret = null, bool captureOutput = true)
        {
            var args = arguments.ToList();
            var result = await base.RunAsync(executable, args, token, workingDirectory, environment, onOutput, secret, captureOutput);
            if (!args.Contains("restore")) return result;
            result.EnsureSuccess("Native fixture restore"); Restores++;
            if (Restores > 1 && mode != "persistent") return result;
            var file = Path.Combine(args[args.IndexOf("--target") + 1], name);
            if (mode == "hardlink")
            {
                File.Delete(file);
                static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
                (await new ProcessRunner().RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
                    "New-Item -ItemType HardLink -Path " + Quote(file) + " -Target " + Quote(outside) + " -ErrorAction Stop | Out-Null"])).EnsureSuccess("Link fixture original");
            }
            else await File.WriteAllTextAsync(file, mode == "content" ? new string('X', (int)new FileInfo(file).Length) : "Interference after the original transfer and before completion\n", token);
            var reported = mode == "outside" ? original : file;
            var message = mode == "content" ? $"Unexpected content in {reported}, starting at offset 0" : $"Invalid file size for {reported}: expected 24, got {new FileInfo(file).Length}";
            return new(1, "", JsonSerializer.Serialize(new { message_type = "error", error = new { message }, during = "restore", item = reported }) + "\n" +
                JsonSerializer.Serialize(new { message_type = "exit_error", code = 1, message = "Fatal: There were 1 errors" }));
        }
    }
}
