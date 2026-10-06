using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Storage;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class CliDestinationTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        var paths = new AppPaths(Path.Combine(root, "catalog"));
        var repository = Path.Combine(root, "password-free");
        await tests.Run("CLI defaults to password-free creation and replacement-computer opening", async () =>
        {
            var result = await Run(paths, "destination-add", "--name", "Drive", "--path", repository); result.EnsureSuccess("Create password-free CLI destination");
            var destination = Json.Read<Destination>(result.Output);
            Assert(destination.Protection == DestinationProtection.PasswordFree && result.Error.Length == 0 && !Directory.EnumerateFiles(paths.CredentialsDirectory).Any(), "CLI default required a credential.");
            var freshPaths = new AppPaths(Path.Combine(root, "fresh"));
            result = await Run(freshPaths, "destination-open", "--name", "Recovered", "--path", repository); result.EnsureSuccess("Open password-free CLI repository");
            Assert(Json.Read<Destination>(result.Output).RepositoryId == destination.RepositoryId, "CLI opened a different repository.");
            var export = Path.Combine(root, "password-free.key");
            result = await Run(paths, "key-export", "--destination", destination.Id, "--output", export);
            Assert(result.ExitCode == 1 && result.Error.Contains("no recovery key") && !File.Exists(export), "Password-free export did not give a useful error.");
        });
        await tests.Run("CLI protected creation warns, exports a key and infers protection from its file", async () =>
        {
            var protectedRepository = Path.Combine(root, "protected");
            var result = await Run(paths, "destination-add", "--name", "Protected", "--path", protectedRepository, "--protection", "recovery-key"); result.EnsureSuccess("Create protected CLI repository");
            var destination = Json.Read<Destination>(result.Output);
            Assert(destination.Protection == DestinationProtection.RecoveryKey && result.Error.Contains("If you lose all copies"), "CLI protected mode omitted its recovery warning.");
            var file = Path.Combine(root, "recovery.key");
            (await Run(paths, "key-export", "--destination", destination.Id, "--output", Path.GetRelativePath(Environment.CurrentDirectory, file))).EnsureSuccess("CLI key export");
            var fresh = new AppPaths(Path.Combine(root, "fresh-protected"));
            result = await Run(fresh, "destination-open", "--name", "Recovered", "--path", protectedRepository, "--key-file", file); result.EnsureSuccess("Open protected CLI repository");
            Assert(Json.Read<Destination>(result.Output).Protection == DestinationProtection.RecoveryKey, "Key-file inference lost protection.");
            var key = await File.ReadAllTextAsync(file);
            Assert(!new CatalogStore(paths).Export().Contains(key) && !result.Output.Contains(key) && !result.Error.Contains(key), "CLI or catalog exposed a recovery key.");
        });
        await tests.Run("CLI rejects conflicting and unknown protection without changing destinations", async () =>
        {
            var catalog = new CatalogStore(paths); var before = catalog.Export(); var target = Path.Combine(root, "invalid");
            var result = await Run(paths, "destination-add", "--name", "Invalid", "--path", target, "--protection", "password-free", "--key-file", "does-not-exist.key");
            Assert(result.ExitCode == 1 && result.Error.Contains("cannot be combined"), "Conflicting CLI protection was not rejected before reading a key.");
            result = await Run(paths, "destination-add", "--name", "Invalid", "--path", target, "--protection", "unsupported");
            Assert(result.ExitCode == 1 && !Directory.Exists(target) && catalog.Export() == before, "Unknown protection created repository state.");
        });
    }

    private static Task<ProcessResult> Run(AppPaths paths, params string[] arguments) =>
        NativeProcessFixture.RunAsync(NativeProcessFixture.ProgramPath("RepoBackup.Cli"), arguments.Concat(["--data-dir", paths.DataDirectory]).ToArray());
}
