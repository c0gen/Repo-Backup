# Repo Backup

A Windows desktop application for encrypted, versioned backups of Git repositories and project folders.

Repo Backup protects the work on your computer: committed history, staged and unstaged changes, untracked files, configuration, assets, and associated Git worktrees. It uses restic to store encrypted, deduplicated snapshots on a local drive, external drive, or UNC network share. Browse earlier versions and restore an entire project, a folder, or a single file from the desktop app or CLI.

Built with C# / .NET 10 and WPF, the desktop app, command-line interface, and scheduled runner share the same backup services and SQLite catalog.

![Repo Backup project dashboard with source preview and backup status](docs/images/project-dashboard.png)

*The dashboard shown above was rendered from an isolated synthetic fixture. Regenerate it after building with `powershell -ExecutionPolicy Bypass -File .\scripts\Generate-SyntheticScreenshot.ps1`.*

## Features

- **Project discovery:** find projects from Codex, Claude Code, and Antigravity, scan for Git repositories, or add any folder manually. Discovered projects enter a review inbox before they can be backed up.
- **Git-aware backups:** full-project snapshots include local Git history, the index, working files, linked worktrees, and local Git LFS and submodule data.
- **Folder selections and previews:** save partial selections, inspect file counts and size estimates, and customize dependency and cache exclusions. Tracked files are preserved; `.gitignore` does not determine backup contents.
- **Versioned recovery:** keep the latest ten successful snapshots per project or saved selection. Partial selections have their own retention series, so they cannot displace full-project recovery points.
- **Verification and restore:** verify repository data, test restores, and recover files into a new directory. Backup, verification, and cleanup results are recorded separately.
- **Optional scheduling:** configure daily, weekly, or interval backups through Windows Task Scheduler. The hidden runner works while the GUI is closed, catches up missed runs, and prevents overlapping scheduled jobs.
- **Portable recovery:** encrypted snapshots include recovery manifests. Open an existing repository with its exported recovery key and rebuild the catalog on a replacement computer.
- **Optional discovery hooks:** register newly used folders through native Codex, Claude Code, or Antigravity hooks. Hooks create review candidates; they do not enable projects or start backups.

## Requirements

- Windows x64.
- Git for Windows available on `PATH` for Git inspection and recovery.
- A writable backup destination with enough space: a local folder, external drive, or UNC share accessible to the signed-in Windows user.

Published packages include the .NET runtime and pinned restic executable. End users do not need to install .NET separately. Building from source requires Windows PowerShell or PowerShell 7 and internet access for the initial SDK, restic, and NuGet downloads.

## Build and run

From the repository root, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -Publish
.\dist\RepoBackup\RepoBackup.exe
```

The build script bootstraps the pinned .NET SDK **10.0.401** into `.tools`, downloads and verifies restic **0.18.0**, restores the dependencies, and builds the solution. The SDK archive is checked with SHA-512; the restic archive and executable are checked with SHA-256. NuGet versions are recorded in committed `packages.lock.json` files.

Publishing produces:

| Output | Contents |
| --- | --- |
| `dist/RepoBackup/` | Self-contained Windows x64 app, CLI, hidden runner, .NET runtime, native SQLite, restic and its license, installer, documentation, and SHA-256 package manifest |
| `dist/RepoBackup-win-x64.zip` | Distributable archive of the published application |

For a build without packaging, omit `-Publish`. Build outputs, downloaded tools, test artifacts, and published packages are ignored by Git.

### Optional per-user installation

After publishing, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\dist\RepoBackup\Install.ps1
```

The installer verifies the package hashes, copies the application to `%LOCALAPPDATA%\Programs\RepoBackup`, and creates a Start menu shortcut. Use this stable installation path for scheduling and discovery hooks. Close the installed app and wait for scheduled operations to finish before updating it.

## First backup

1. Open **Destinations**, choose **Add / open repository**, and create a repository in an empty folder on your backup drive or share.
2. Export the destination's recovery key and keep it somewhere safe, separate from the backup repository. The key is required for recovery on another computer.
3. Review candidates in **Discoveries**, add a folder manually, or scan for Git repositories. Enable the projects you want to protect.
4. In **Projects**, select a destination and review the source preview and exclusions. Use **Back Up Selected** or **Back Up All Enabled**.
5. Check the backup, verification, and cleanup results in **Activity**. Use **Snapshots & Restore** to browse recovery points or perform a test restore.

Scheduling starts disabled. Enable it in **Settings** after configuring the projects and destination you want to use.

## Backup contents and recovery

Full-project backups include Git metadata, unfinished changes, untracked and ignored configuration such as `.env`, and project assets. Known dependency and cache directories such as `node_modules`, `.venv`, `__pycache__`, and `obj` are excluded by default, with tracked files preserved. Generic `build` and `dist` folders remain included unless you explicitly exclude them. Review the preview to see the rules for each project or saved selection.

Credentials are protected with Windows DPAPI for the current user. The catalog, credentials, job metadata, cache, and locks live under `%LOCALAPPDATA%\RepoBackup`. Catalog exports omit credentials; export the recovery key separately. Pass `--data-dir <absolute-directory>` to the app or CLI to use an isolated catalog.

Restores use a new directory. Git recovery reconstructs worktree and submodule paths inside the restored copy without modifying the original repository. The backup repository and recovery key are sufficient to rebuild a catalog on a replacement computer.

See [Recovery instructions](docs/RECOVERY.md) for full and selective restores, replacement-computer recovery, and verification details.

## Command-line usage

The packaged CLI is `dist/RepoBackup/RepoBackup.Cli.exe`. From the repository root:

```powershell
$cli = '.\dist\RepoBackup\RepoBackup.Cli.exe'
& $cli help
& $cli discover
& $cli discover --source claude-code
& $cli add-folder --path 'C:\Projects\Example' --name 'Example'
& $cli destination-add --name 'Backup Drive' --path 'E:\RepoBackups'
& $cli projects
& $cli destinations
```

Copy the destination ID returned by `destinations`, then use it to back up enabled projects and check their snapshots:

```powershell
$destinationId = 'paste-destination-id-here'
& $cli backup --destination $destinationId --all-enabled
& $cli snapshots --destination $destinationId
& $cli verify --destination $destinationId
```

The CLI also supports previews, saved selections, catalog import/export, schedules, recovery-key export, and restore operations. Existing repositories are opened with `destination-open --key-file <file>`; recovery keys are supplied as files rather than command-line passwords. Run `help` for the full command list and see [Discovery and hooks](docs/DISCOVERY.md) for provider-specific paths and overrides.

| Exit code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Command error |
| `2` | Incomplete or failed backup, verification, or backup cleanup |
| `130` | Cancellation |

## Development and testing

Build and run the acceptance suite:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -Test
```

To also test the hidden runner through an isolated Windows scheduled task and produce a distributable package:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -Test -WindowsIntegration -Publish
```

The executable test suite uses real restic repositories and generated Git fixtures under `.artifacts`. It covers discovery, catalog migration, native hook callbacks, working-file hashes, Git worktrees and submodules, selective restores, retention, cancellation, destination locks, and recovery with a fresh catalog. Tests do not back up user projects.

After building the solution, run the smaller catalog, discovery, CLI, and hook suite with:

```powershell
.\tests\RepoBackup.Tests\bin\Release\net10.0-windows\RepoBackup.Tests.exe --unit
```

The test executable also accepts `--edge-only` for the unit checks plus link, low-space, live-change, and external Git-data tests. [Validation notes](docs/VALIDATION.md) describe coverage and hardware checks.

### Project structure

| Path | Responsibility |
| --- | --- |
| [`src/RepoBackup.Core`](src/RepoBackup.Core) | Shared application services, SQLite catalog, discovery, selection rules, restic integration, Git recovery, credentials, and scheduling |
| [`src/RepoBackup.Desktop`](src/RepoBackup.Desktop) | WPF desktop interface, theme, views, and view models |
| [`src/RepoBackup.Cli`](src/RepoBackup.Cli) | Interactive command-line interface |
| [`src/RepoBackup.Runner`](src/RepoBackup.Runner) | Hidden scheduled and discovery-hook runner |
| [`tests/RepoBackup.Tests`](tests/RepoBackup.Tests) | Executable unit, backup/recovery, and Windows integration fixtures |
| [`scripts`](scripts) | SDK bootstrap, dependency downloads, build, packaging, and installation |
| [`docs`](docs) | Discovery, recovery, and validation guides |
| [`vendor/restic`](vendor/restic) | Restic license and locally downloaded backup executable |

## Scope and operating limits

The implemented scope is one Windows user backing up projects to local or UNC destinations. Cloud backends, bidirectional synchronization, and whole-machine recovery are outside the current scope.

Live backups can observe files changing during a run and cannot guarantee that every file represents a single instant. Unavailable drives, unreadable files, unresolved Git data, and unhydrated cloud placeholders can produce incomplete coverage. Failed or incomplete backups do not trigger retention cleanup. An explicit VSS mode is available for elevated backups; it requires Administrator rights. Restoring Windows symbolic links may require Developer Mode or elevation.

Discovery depends on each program's local storage format. Invalid sources generate provider-specific warnings while the other providers continue. Only project metadata is retained; discovery does not persist conversations, credentials, or hook payloads. Native hooks are optional and follow each program's own review flow. Antigravity hooks require a build with native Hooks support.

## Documentation

- [Recovery and replacement-computer setup](docs/RECOVERY.md)
- [Project discovery and optional hooks](docs/DISCOVERY.md)
- [Validation coverage and operating limits](docs/VALIDATION.md)

## Third-party software

Repo Backup uses restic as its backup engine. Its license is included at [`vendor/restic/LICENSE`](vendor/restic/LICENSE) and distributed with the application. The solution also uses NuGet dependencies recorded in the project files and lock files.

## License

Repo Backup is licensed under the [MIT License](LICENSE).
