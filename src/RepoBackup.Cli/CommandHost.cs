using RepoBackup.Core.Application;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Windows;

namespace RepoBackup.Cli;

public static class CommandHost
{
    public const string Help = """
        Repo Backup — versioned Windows project recovery
        Global: --data-dir <directory> (default %LOCALAPPDATA%\RepoBackup)
        discover [--source all|codex|claude-code|antigravity|vscode|copilot]
          [--codex-state <file>] [--codex-home <directory>] [--vscode-user-data <directory>]
          [--claude-config <file>] [--claude-projects <directory>] [--antigravity-user-data <directory>]
        discover --state <file> (Codex only)      scan --path <folder>
        register --path <folder> [--name <name>]  add-folder --path <folder> [--name <name>]
        projects                                approve --project <id> [--disable]
        relink --project <id> --root <id> --path <new folder>
        destination-add --name <name> --path <empty folder>
          [--protection password-free|recovery-key] [--key-file <recovery key>]
        destination-open --name <name> --path <repository>
          [--protection password-free|recovery-key] [--key-file <recovery key>]
        Protection defaults to password-free, or recovery-key when --key-file is supplied.
        destinations                            key-export --destination <id> --output <file>
        backup --destination <id> (--all-enabled | --project <id> | --selection <id>) [--vss]
        preview --project <id> [--selection <id>]
        snapshots --destination <id>            files --destination <id> --snapshot <id>
        restore --destination <id> --snapshot <id> --target <NEW directory> [--path <snapshot path>]
        verify --destination <id>               recover --destination <id>
        export --output <file>                  import --input <file>
        history                                 selection-save --project <id> --name <name> --paths <semicolon-separated paths>
        schedule-save --destination <id> --kind <Daily|Weekly|Interval> [--time HH:mm] [--day Friday] [--hours 24] [--selection <id>] [--disabled]
        schedule-delete --id <id>               run-schedule --id <id>
        install-hook [--source codex|claude-code|antigravity] [--config <file>]
        register-hook [--source codex|claude-code|antigravity] (JSON stdin; candidates only; default Codex)
        hook-status                             (configured versus verified hooks)
        """;

    public static async Task<int> RunAsync(string[] arguments, TextWriter output, TextWriter errors, CancellationToken token = default)
    {
        try
        {
            var args = arguments.ToList();
            string? Option(string name)
            {
                var index = args.IndexOf(name);
                if (index < 0) return null;
                if (index + 1 >= args.Count || args[index + 1].StartsWith("--")) throw new ArgumentException("Missing value for " + name);
                var value = args[index + 1]; args.RemoveRange(index, 2); return value;
            }
            bool Flag(string name) => args.Remove(name);
            var dataDirectory = Option("--data-dir");
            if (args.Count == 0 || args[0] is "help" or "--help" or "-h") { await output.WriteLineAsync(Help); return 0; }
            var command = args[0]; args.RemoveAt(0);
            var values = new Dictionary<string, string>(); var flags = new HashSet<string>();
            while (args.Count > 0)
            {
                var name = args[0];
                if (name is "--all-enabled" or "--vss" or "--disable" or "--disabled") { Flag(name); flags.Add(name); }
                else if (name.StartsWith("--", StringComparison.Ordinal)) values.Add(name, Option(name)!);
                else throw new ArgumentException("Unexpected argument " + name);
            }
            string Required(string name) => values.GetValueOrDefault(name) ?? throw new ArgumentException("Required option " + name);
            string? Optional(string name) => values.GetValueOrDefault(name);
            if (command == "register-hook")
            {
                await HookRegistration.RegisterAsync(Optional("--source") ?? DiscoveryProviders.Codex, new AppPaths(dataDirectory), Console.In, token);
                return 0;
            }
            var app = new ApplicationServices(dataDirectory);
            ProjectEntry Project() => app.Catalog.Projects().SingleOrDefault(p => p.Id == Required("--project")) ?? throw new ArgumentException("Unknown project.");
            Destination Destination() => app.Catalog.Destinations().SingleOrDefault(d => d.Id == Required("--destination")) ?? throw new ArgumentException("Unknown destination.");
            SavedSelection? Selection() => Optional("--selection") is { } id ? app.Catalog.Selections().SingleOrDefault(s => s.Id == id) ?? throw new ArgumentException("Unknown saved selection.") : null;
            async Task Print<T>(T value) => await output.WriteLineAsync(Json.Write(value));
            switch (command)
            {
                case "discover":
                    var legacyState = Optional("--state"); var source = Optional("--source") ?? (legacyState is null ? "all" : DiscoveryProviders.Codex);
                    if (legacyState is not null && (source != DiscoveryProviders.Codex || Optional("--codex-state") is not null || Optional("--codex-home") is not null || Optional("--vscode-user-data") is not null))
                        throw new ArgumentException("--state is a Codex-only compatibility option. Use --codex-state with --source all.");
                    await Print(await app.Discovery.RefreshAsync(new DiscoveryOptions
                    {
                        Source = source, CodexStatePath = legacyState ?? Optional("--codex-state"), CodexHomeDirectory = Optional("--codex-home"),
                        ClaudeConfigPath = Optional("--claude-config"), ClaudeProjectsDirectory = Optional("--claude-projects"),
                        AntigravityUserDataDirectory = Optional("--antigravity-user-data"), VsCodeUserDataDirectory = Optional("--vscode-user-data")
                    }, token)); break;
                case "scan": await Print(new { candidates = await app.Discovery.ScanAsync(Required("--path"), token) }); break;
                case "register": case "add-folder":
                    var path = PathSafety.Normalize(Required("--path")); await Print(app.Catalog.RegisterCandidate(Optional("--name") ?? PathSafety.DisplayName(path), [path], "Manual", approved: command == "add-folder")); break;
                case "projects": await Print(app.Catalog.Projects()); break;
                case "approve": var project = Project(); app.Catalog.SaveProject(project with { Reviewed = true, Dismissed = false, Enabled = !flags.Contains("--disable") }); break;
                case "relink": app.Catalog.RelinkRoot(Project().Id, Required("--root"), Required("--path")); break;
                case "destination-add": case "destination-open":
                    var keyFile = Optional("--key-file");
                    var protection = Optional("--protection") switch
                    {
                        null => keyFile is null ? DestinationProtection.PasswordFree : DestinationProtection.RecoveryKey,
                        "password-free" => DestinationProtection.PasswordFree,
                        "recovery-key" => DestinationProtection.RecoveryKey,
                        _ => throw new ArgumentException("Choose --protection password-free or recovery-key.")
                    };
                    if (protection == DestinationProtection.PasswordFree && keyFile is not null)
                        throw new ArgumentException("--key-file cannot be combined with --protection password-free.");
                    var key = keyFile is null ? null : (await File.ReadAllTextAsync(keyFile, token)).Trim();
                    if (command == "destination-add" && protection == DestinationProtection.RecoveryKey)
                        await errors.WriteLineAsync(BackupService.RecoveryKeyWarning);
                    await Print(await app.Backups.AddDestinationAsync(Required("--name"), Required("--path"), key, command == "destination-open", token, protection)); break;
                case "destinations": await Print(app.Catalog.Destinations()); break;
                case "key-export":
                    await app.Backups.ExportRecoveryKeyAsync(Destination(), Path.GetFullPath(Required("--output")), token: token);
                    await output.WriteLineAsync("Recovery key exported. Store it separately from the backup drive."); break;
                case "preview": await Print(await app.Planner.PreviewAsync(Project(), Selection(), token)); break;
                case "backup":
                    var selection = Selection();
                    var projects = selection is not null ? app.Catalog.Projects().Where(p => p.Id == selection.ProjectId) : flags.Contains("--all-enabled") ? app.Catalog.Projects().Where(p => p.Enabled && p.Reviewed && !p.Dismissed) : [Project()];
                    var jobs = await app.Backups.RunAsync(Destination(), projects, selection, token: token, useVss: flags.Contains("--vss")); await Print(jobs);
                    return RunOutcome.ExitCode(jobs);
                case "snapshots": await Print(await app.Restic.SnapshotsAsync(Destination(), token)); break;
                case "files": await Print(await app.Restic.FilesAsync(Destination(), Required("--snapshot"), token)); break;
                case "restore":
                    var destination = Destination(); var snapshot = (await app.Restic.SnapshotsAsync(destination, token)).Single(s => s.Id.StartsWith(Required("--snapshot"), StringComparison.Ordinal));
                    await output.WriteLineAsync(await app.Restore.RestoreAsync(destination, snapshot, Required("--target"), Optional("--path"), token: token)); break;
                case "verify": var verification = await app.Backups.VerifyAsync(Destination(), token); await Print(verification); return verification.Outcome == Outcome.Cancelled ? 130 : verification.Outcome == Outcome.Successful ? 0 : 2;
                case "recover": await Print(new { rebuiltProjects = await app.Restore.RebuildCatalogAsync(Destination(), token) }); break;
                case "export": await File.WriteAllTextAsync(Required("--output"), app.Catalog.Export(), token); break;
                case "import": app.Catalog.Import(await File.ReadAllTextAsync(Required("--input"), token)); break;
                case "history": await Print(app.Catalog.History()); break;
                case "selection-save":
                    var saved = new SavedSelection { ProjectId = Project().Id, Name = Required("--name"), Paths = Required("--paths").Split(';').Select(PathSafety.Normalize).ToList() };
                    await app.Planner.PreviewAsync(Project(), saved, token); app.Catalog.SaveSelection(saved); await Print(saved); break;
                case "schedule-save":
                    var schedule = new ScheduleDefinition { Id = Optional("--id") ?? Guid.NewGuid().ToString("N"), DestinationId = Destination().Id, SelectionId = Selection()?.Id, Kind = Enum.Parse<ScheduleKind>(Required("--kind"), true), Time = TimeOnly.Parse(Optional("--time") ?? "18:00"), Day = Enum.Parse<DayOfWeek>(Optional("--day") ?? "Friday", true), IntervalHours = int.Parse(Optional("--hours") ?? "24"), Enabled = !flags.Contains("--disabled") };
                    await app.Scheduler.SaveAsync(schedule, token); await Print(schedule); break;
                case "schedule-delete": await app.Scheduler.DeleteAsync(Required("--id"), token); break;
                case "run-schedule":
                    var scheduled = app.Catalog.Schedules().Single(s => s.Id == Required("--id")); if (!scheduled.Enabled) return 0;
                    var scheduledSelection = scheduled.SelectionId is { } selectedId ? app.Catalog.Selections().Single(s => s.Id == selectedId) : null;
                    var scheduledProjects = app.Catalog.Projects().Where(p => p.Reviewed && !p.Dismissed && (scheduledSelection is null ? p.Enabled : p.Id == scheduledSelection.ProjectId));
                    var scheduledJobs = await app.Backups.RunAsync(app.Catalog.Destinations().Single(d => d.Id == scheduled.DestinationId), scheduledProjects, scheduledSelection, token: token);
                    return RunOutcome.ExitCode(scheduledJobs);
                case "install-hook":
                    var hookSource = Optional("--source") ?? DiscoveryProviders.Codex;
                    DiscoveryHookInstaller.Install(hookSource, app.Catalog, app.Paths.DataDirectory, configurationPath: Optional("--config"));
                    await output.WriteLineAsync("Hook configured. " + DiscoveryHookInstaller.Instructions(hookSource)); break;
                case "hook-status": await Print(DiscoveryProviders.HookCapable.ToDictionary(p => p, p => DiscoveryHookInstaller.Status(app.Catalog, p))); break;
                default: throw new ArgumentException("Unknown command. Run help for available commands.");
            }
            return 0;
        }
        catch (OperationCanceledException) { await errors.WriteLineAsync("Operation cancelled."); return 130; }
        catch (Exception e) { await errors.WriteLineAsync(e.Message); return 1; }
    }
}
