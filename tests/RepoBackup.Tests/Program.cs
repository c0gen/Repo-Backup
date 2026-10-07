using RepoBackup.Tests;
using RepoBackup.Core.Models;

if (args.FirstOrDefault() == "--public-screenshot-fixture")
{
    if (args.Length != 2) throw new ArgumentException("Pass an isolated fixture root after --public-screenshot-fixture.");
    await PublicScreenshotFixture.PrepareAsync(args[1]);
    return 0;
}

var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".artifacts", "tests", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]));
var runner = new TestRunner();
await RestoreProgressTests.RunAsync(runner);
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
}
if (args.Contains("--windows-integration")) await WindowsIntegrationTests.RunAsync(runner, root);
Console.WriteLine("Test artifacts: " + root);
return runner.Finish();
