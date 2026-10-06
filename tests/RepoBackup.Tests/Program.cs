using RepoBackup.Tests;

if (args.FirstOrDefault() == "--public-screenshot-fixture")
{
    if (args.Length != 2) throw new ArgumentException("Pass an isolated fixture root after --public-screenshot-fixture.");
    await PublicScreenshotFixture.PrepareAsync(args[1]);
    return 0;
}

var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".artifacts", "tests", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]));
var runner = new TestRunner();
await CatalogTests.RunAsync(runner, root);
await DiscoveryTests.RunAsync(runner, Path.Combine(root, "discovery"));
await ProjectIdentityTests.RunAsync(runner, Path.Combine(root, "identity"));
await CatalogMigrationTests.RunAsync(runner, Path.Combine(root, "migration"));
await HookTests.RunAsync(runner, Path.Combine(root, "hooks"));
await CliDiscoveryTests.RunAsync(runner, Path.Combine(root, "cli"));
if (!args.Contains("--unit"))
{
    if (!args.Contains("--edge-only")) await BackupTests.RunAsync(runner, new Fixture(Path.Combine(root, "recovery")));
    await EdgeCaseTests.RunAsync(runner, Path.Combine(root, "edge-cases"));
}
if (args.Contains("--windows-integration")) await WindowsIntegrationTests.RunAsync(runner, root);
Console.WriteLine("Test artifacts: " + root);
return runner.Finish();
