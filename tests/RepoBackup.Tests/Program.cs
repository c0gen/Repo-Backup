using RepoBackup.Tests;
using RepoBackup.Core.Models;

if (args.FirstOrDefault() is "--backup-benchmark" or "--backup-benchmark-continue")
{
    if (args.Length != 2) throw new ArgumentException("Pass the absolute benchmark fixture directory.");
    await BackupBenchmark.RunAsync(Path.GetFullPath(args[1]), args[0] == "--backup-benchmark-continue");
    return 0;
}

if (args.FirstOrDefault() == "--public-screenshot-fixture")
{
    if (args.Length != 2) throw new ArgumentException("Pass an isolated fixture root after --public-screenshot-fixture.");
    await PublicScreenshotFixture.PrepareAsync(args[1]);
    return 0;
}

var artifactBase = Environment.GetEnvironmentVariable("REPOBACKUP_TEST_ROOT") ?? Path.Combine(Environment.CurrentDirectory, ".artifacts", "tests");
var root = Path.GetFullPath(Path.Combine(artifactBase, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]));
var runner = new TestRunner();
if (args.Contains("--restore-repair-only"))
{
    await RestoreVerificationRepairTests.RunAsync(runner, Path.Combine(root, "restore-repair"));
    Console.WriteLine("Test artifacts: " + root);
    return runner.Finish();
}
if (args.Contains("--restore-links-only"))
{
    await RestoreLinkRequirementsTests.RunAsync(runner, Path.Combine(root, "restore-links"));
    Console.WriteLine("Test artifacts: " + root);
    return runner.Finish();
}
if (args.Contains("--locks-only"))
{
    await ResticLockRecoveryTests.RunAsync(runner, Path.Combine(root, "lock-recovery"));
    Console.WriteLine("Test artifacts: " + root);
    return runner.Finish();
}
if (args.Contains("--long-paths-only"))
{
    await LongGitPathTests.RunAsync(runner, Path.Combine(root, "long-git"));
    Console.WriteLine("Test artifacts: " + root);
    return runner.Finish();
}
if (args.Contains("--legacy-only"))
{
    await LegacyRecoveryRegressionTests.RunAsync(runner, Path.Combine(root, "legacy-recovery"));
    Console.WriteLine("Test artifacts: " + root);
    return runner.Finish();
}
if (args.Contains("--blockers-only"))
{
    await CatalogIdentityRegressionTests.RunAsync(runner, Path.Combine(root, "identity-blockers"));
    await CoverageDiagnosticTests.RunAsync(runner, Path.Combine(root, "coverage-blockers"));
    await SnapshotSummaryTests.RunAsync(runner);
    await ResticLockRecoveryTests.RunAsync(runner, Path.Combine(root, "lock-recovery"));
    await GitBackupBlockerTests.RunAsync(runner, Path.Combine(root, "git-blockers"));
    Console.WriteLine("Test artifacts: " + root);
    return runner.Finish();
}
if (args.Contains("--queue-only"))
{
    await BackupQueueTests.RunAsync(runner, Path.Combine(root, "queue"));
    await BackupProgressTests.RunAsync(runner, Path.Combine(root, "backup-progress"));
    await BackupQueueIntegrationTests.RunAsync(runner, Path.Combine(root, "queue-integration"));
    await DesktopDestinationTests.RunAsync(runner, Path.Combine(root, "desktop-destinations"), queueOnly: true);
    Console.WriteLine("Test artifacts: " + root);
    return runner.Finish();
}
if (args.Contains("--efficiency-only"))
{
    await BackupEfficiencyTests.RunAsync(runner, Path.Combine(root, "efficiency"));
    Console.WriteLine("Test artifacts: " + root);
    return runner.Finish();
}
if (args.Contains("--audit-only"))
{
    await SourceSafetyRegressionTests.RunAsync(runner, Path.Combine(root, "source-safety"));
    await SelectionRulesRegressionTests.RunAsync(runner, Path.Combine(root, "selection-rules"));
    await DestinationReplacementTests.RunAsync(runner, Path.Combine(root, "destination-replacements"));
    await LegacyRecoveryRegressionTests.RunAsync(runner, Path.Combine(root, "legacy-recovery"));
    await SchedulerArgumentTests.RunAsync(runner);
    await DesktopDestinationTests.RunAsync(runner, Path.Combine(root, "desktop-destinations"));
    Console.WriteLine("Test artifacts: " + root);
    return runner.Finish();
}
if (args.Contains("--wsl-only") || args.Contains("--wsl-native-only"))
{
    await WslLinkTests.RunAsync(runner, Path.Combine(root, "wsl-native"));
    if (!args.Contains("--wsl-native-only")) await WslLinkBackupTests.RunAsync(runner, Path.Combine(root, "wsl-backup"));
    Console.WriteLine("Test artifacts: " + root);
    return runner.Finish();
}
await RestoreProgressTests.RunAsync(runner);
await RestoreLinkRequirementsTests.RunAsync(runner, Path.Combine(root, "restore-links"));
await BackupQueueTests.RunAsync(runner, Path.Combine(root, "queue"));
await BackupProgressTests.RunAsync(runner, Path.Combine(root, "backup-progress"));
DesktopWorkflowRegressionTests.IncludeScheduling = args.Contains("--windows-integration");
if (args.Contains("--reliability-only"))
{
    await ReliabilityRegressionTests.RunAsync(runner, Path.Combine(root, "reliability-regressions"));
    Console.WriteLine("Test artifacts: " + root);
    return runner.Finish();
}
if (args.Contains("--destinations-only"))
{
    await DesktopDestinationTests.RunAsync(runner, Path.Combine(root, "desktop-destinations"));
    await DestinationTests.RunAsync(runner, Path.Combine(root, "destinations"));
    await CliDestinationTests.RunAsync(runner, Path.Combine(root, "cli-destinations"));
    Console.WriteLine("Test artifacts: " + root);
    return runner.Finish();
}
if (args.Contains("--backup-only"))
{
    await BackupTests.RunAsync(runner, new Fixture(Path.Combine(root, "recovery")));
    await BackupTests.RunAsync(runner, new Fixture(Path.Combine(root, "password-free-recovery")), DestinationProtection.PasswordFree);
    Console.WriteLine("Test artifacts: " + root);
    return runner.Finish();
}
await CatalogTests.RunAsync(runner, root);
await DiscoveryTests.RunAsync(runner, Path.Combine(root, "discovery"));
await CodexDiscoveryTests.RunAsync(runner, Path.Combine(root, "codex-discovery"));
await VsCodeDiscoveryTests.RunAsync(runner, Path.Combine(root, "vscode-discovery"));
await CopilotDiscoveryTests.RunAsync(runner, Path.Combine(root, "copilot-discovery"));
await ProjectIdentityTests.RunAsync(runner, Path.Combine(root, "identity"));
await CatalogMigrationTests.RunAsync(runner, Path.Combine(root, "migration"));
await HookTests.RunAsync(runner, Path.Combine(root, "hooks"));
await CliDiscoveryTests.RunAsync(runner, Path.Combine(root, "cli"));
if (!args.Contains("--unit"))
{
    await RestoreVerificationRepairTests.RunAsync(runner, Path.Combine(root, "restore-repair"));
    await CatalogIdentityRegressionTests.RunAsync(runner, Path.Combine(root, "identity-blockers"));
    await CoverageDiagnosticTests.RunAsync(runner, Path.Combine(root, "coverage-blockers"));
    await SnapshotSummaryTests.RunAsync(runner);
    await ResticLockRecoveryTests.RunAsync(runner, Path.Combine(root, "lock-recovery"));
    await GitBackupBlockerTests.RunAsync(runner, Path.Combine(root, "git-blockers"));
    await BackupEfficiencyTests.RunAsync(runner, Path.Combine(root, "efficiency"));
    await BackupQueueIntegrationTests.RunAsync(runner, Path.Combine(root, "queue-integration"));
    await SourceSafetyRegressionTests.RunAsync(runner, Path.Combine(root, "source-safety"));
    await SelectionRulesRegressionTests.RunAsync(runner, Path.Combine(root, "selection-rules"));
    await DestinationReplacementTests.RunAsync(runner, Path.Combine(root, "destination-replacements"));
    await LegacyRecoveryRegressionTests.RunAsync(runner, Path.Combine(root, "legacy-recovery"));
    await SchedulerArgumentTests.RunAsync(runner);
    await DesktopDestinationTests.RunAsync(runner, Path.Combine(root, "desktop-destinations"));
    await DestinationTests.RunAsync(runner, Path.Combine(root, "destinations"));
    await CliDestinationTests.RunAsync(runner, Path.Combine(root, "cli-destinations"));
    if (!args.Contains("--edge-only"))
    {
        await BackupTests.RunAsync(runner, new Fixture(Path.Combine(root, "recovery")));
        await BackupTests.RunAsync(runner, new Fixture(Path.Combine(root, "password-free-recovery")), DestinationProtection.PasswordFree);
    }
    await ReliabilityRegressionTests.RunAsync(runner, Path.Combine(root, "reliability-regressions"));
    await EdgeCaseTests.RunAsync(runner, Path.Combine(root, "edge-cases"));
    await WslLinkTests.RunAsync(runner, Path.Combine(root, "wsl-native"));
    await WslLinkBackupTests.RunAsync(runner, Path.Combine(root, "wsl-backup"));
}
if (args.Contains("--windows-integration")) await WindowsIntegrationTests.RunAsync(runner, root);
Console.WriteLine("Test artifacts: " + root);
return runner.Finish();
