using System.Diagnostics;
using System.Security.Cryptography;
using RepoBackup.Core.Application;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Tests;

public static class BackupBenchmark
{
    public static async Task RunAsync(string root, bool continueExisting = false)
    {
        Directory.CreateDirectory(root);
        var app = new ApplicationServices(Path.Combine(root, "catalog"), discoveryProviders: []);
        if (continueExisting)
        {
            if (!File.Exists(Path.Combine(root, "measurements.jsonl"))) throw new ArgumentException("Continue requires a generated benchmark fixture.");
            var large = app.Catalog.Projects().Single(p => p.Name == "Large fixture");
            var existingBatch = app.Catalog.Projects().Where(p => p.Name.StartsWith("Batch ", StringComparison.Ordinal)).ToList();
            if (existingBatch.Count != 20) throw new ArgumentException("Benchmark batch fixture is incomplete.");
            var existingDestination = app.Catalog.Destinations().Single();
            await MeasureExisting("large-warmup", [large]);
            for (var i = 0; i < 3; i++) await MeasureExisting("large-repeat", [large]);
            await MeasureExisting("batch-warmup", existingBatch);
            for (var i = 0; i < 3; i++) await MeasureExisting("batch-retention", existingBatch);
            return;

            async Task MeasureExisting(string scenario, List<ProjectEntry> projects) =>
                await Measure(scenario, projects, existingDestination, "measurements-after.jsonl");
        }
        var source = Path.Combine(root, "large-source"); Directory.CreateDirectory(source);
        for (var i = 0; i < 10_000; i++) await File.WriteAllTextAsync(Path.Combine(source, $"file-{i:D5}.txt"), "Synthetic benchmark file " + i);
        var block = RandomNumberGenerator.GetBytes(1024 * 1024);
        await using (var asset = File.Create(Path.Combine(source, "asset.bin")))
            for (var i = 0; i < 512; i++) await asset.WriteAsync(block);
        var project = app.Catalog.RegisterCandidate("Large fixture", [source], "Manual", approved: true);
        var destination = await app.Backups.AddDestinationAsync("Benchmark", Path.Combine(root, "repository"), protection: DestinationProtection.PasswordFree);
        await Measure("large-warmup", [project], destination);
        for (var i = 0; i < 3; i++) await Measure("large-repeat", [project], destination);
        var batch = new List<ProjectEntry>();
        for (var i = 0; i < 20; i++)
        {
            var folder = Path.Combine(root, "batch", i.ToString()); Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "file.txt"), "Synthetic batch " + i);
            batch.Add(app.Catalog.RegisterCandidate("Batch " + i, [folder], "Manual", approved: true));
        }
        // Populate each retention series, then measure runs that actually forget/prune.
        for (var i = 0; i < 10; i++) await Measure("batch-warmup", batch, destination);
        for (var i = 0; i < 3; i++) await Measure("batch-retention", batch, destination);

        async Task Measure(string scenario, List<ProjectEntry> projects, Destination target, string file = "measurements.jsonl")
        {
            var watch = Stopwatch.StartNew(); var stages = new Dictionary<string, double>();
            var jobs = await app.Backups.RunAsync(target, projects, progress: new ProgressRecorder(p => stages[p.Stage] = watch.Elapsed.TotalSeconds));
            if (jobs.Any(j => !Core.Backup.RunOutcome.IsSuccessful(j))) throw new IOException(Json.Write(jobs));
            var process = Process.GetCurrentProcess(); process.Refresh();
            var result = new { scenario, seconds = watch.Elapsed.TotalSeconds, appPeakBytes = process.PeakWorkingSet64,
                stagingBytes = Directory.EnumerateFiles(app.Paths.JobsDirectory, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length), stages,
                phases = jobs.Select(j => j.PhaseMilliseconds).ToList() };
            var json = System.Text.Json.JsonSerializer.Serialize(result);
            Console.WriteLine(json); await File.AppendAllTextAsync(Path.Combine(root, file), json + "\n");
        }
    }
}
