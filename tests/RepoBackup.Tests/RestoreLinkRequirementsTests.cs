using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Recovery;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class RestoreLinkRequirementsTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        Directory.CreateDirectory(root);
        await tests.Run("Restores without native symbolic links do not require link privileges", () =>
        {
            RestoreLinkRequirements.EnsureSupported(root, [new("/ordinary.txt", "file", 0)], _ => throw new InvalidOperationException("Unnecessary probe"));
            Assert(!Directory.EnumerateFileSystemEntries(root).Any(), "A link-free restore created a probe.");
            return Task.CompletedTask;
        });
        await tests.Run("Missing link privileges report the affected path before transfer and remove the probe", () =>
        {
            try
            {
                RestoreLinkRequirements.EnsureSupported(root, [new("/project/dangling", "symlink", 0)], _ => throw new UnauthorizedAccessException("Fixture privilege denial"));
                throw new InvalidOperationException("Missing privilege was ignored.");
            }
            catch (IOException e)
            {
                Assert(e.Message.Contains("Administrator") && e.Message.Contains("Developer Mode") && e.Message.Contains("/project/dangling") && e.InnerException is UnauthorizedAccessException, e.ToString());
            }
            Assert(!Directory.EnumerateFileSystemEntries(root).Any(), "The failed probe left recovery files behind.");
            return Task.CompletedTask;
        });
        await tests.Run("Dangling restored links are verified without following their missing target", async () =>
        {
            var source = new SourceMapping("links", root, "/links", "links", SourceKind.Project);
            var folder = Path.Combine(root, "sources", "links"); Directory.CreateDirectory(folder);
            var missing = Path.Combine(root, "missing-target"); Directory.CreateDirectory(missing);
            var link = Path.Combine(folder, "dangling");
            static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            (await new ProcessRunner().RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
                "New-Item -ItemType Junction -Path " + Quote(link) + " -Target " + Quote(missing) + " -ErrorAction Stop | Out-Null"])).EnsureSuccess("Create no-privilege dangling-link fixture");
            Directory.Delete(missing);
            var plan = new RestorePathPlan(source, "/links", null, [new("/links/dangling", "symlink", 0)]);
            plan.Verify(root);
            Directory.Delete(link);
            await File.WriteAllTextAsync(link, "Not a link");
            await Throws<IOException>(() => Task.Run(() => plan.Verify(root)));
        });
    }
}
