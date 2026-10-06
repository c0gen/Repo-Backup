# Installer packaging and releases

The Windows installer is built with [Inno Setup](https://jrsoftware.org/isinfo.php). It contains the same self-contained application as the portable ZIP, including the WPF .NET runtime, native SQLite, pinned restic, [MinGit](https://gitforwindows.org/mingit), and Git LFS for local LFS recovery. No prerequisites are downloaded when users install or run the release.

## Build

From the repository root on Windows:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -Test -Installer -Version 1.0.0
```

`-Installer` includes `-Publish`. Without `-Version`, the version comes from `Directory.Build.props`. Versions use `major.minor.patch`, optionally followed by a prerelease suffix such as `1.1.0-rc.1`. The application assemblies, package manifest, installer metadata, and asset filenames all use the requested version.

| Output | Purpose |
| --- | --- |
| `dist/RepoBackup/` | Complete application folder and file hash manifest |
| `dist/RepoBackup-1.0.0-win-x64-setup.exe` | Single offline installer for the current Windows user |
| `dist/RepoBackup-1.0.0-win-x64.zip` | Portable application archive |
| `dist/SHA256SUMS` | SHA-256 checksums of the ZIP and installer |

`scripts/Build-Installer.ps1` can rebuild setup from an existing published folder. It verifies the package manifest and refuses a version mismatch. The publisher starts with a clean application output directory so removed files do not leak into subsequent packages, and refuses to replace a folder containing a running Repo Backup process.

If you are running the app from `dist/RepoBackup`, select a separate staging folder under `dist` without closing it:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -Installer -PackageDirectory .\dist\release\RepoBackup
```

The installer, ZIP, and checksums still go into `dist/`. Pass the same `-PackageDirectory` to `Build-Installer.ps1` when compiling directly from that staging folder.

The compiler is installed in [Inno Setup's portable mode](https://jrsoftware.org/ishelp/topic_technotes.htm) under `.tools`. MinGit is extracted under `.tools` and copied intact into the package's `git/` directory, including its licenses. The pinned Git LFS executable is added beside its private `git.exe`, with its license under `git/lfs/`. Their versions, official download URLs, and SHA-256 digests are pinned in `scripts/packaging/Dependencies.psd1`. Downloads are checked before extraction or execution. These tools and build outputs remain ignored by Git.

## Installation, updates, and removal

Setup installs to `%LOCALAPPDATA%\Programs\RepoBackup`, matching the stable path used by the scheduler and discovery hooks. It requests no elevation, creates a Start menu shortcut, offers an optional desktop shortcut, and registers an uninstaller in Windows Settings. Git is private to the application; setup does not modify the user's Git installation or `PATH`.

The desktop app, CLI, and hidden runner hold a shared named mutex while running. Setup and uninstall check it and require active instances to finish before replacing files. The installer does not forcibly stop running backups. Older builds predating this guard must be closed manually before upgrading.

Reinstalling or upgrading uses the same app identity and path. The catalog and DPAPI credentials are stored separately in `%LOCALAPPDATA%\RepoBackup`; backup repositories live at the destinations the user configured. Uninstall removes program files and shortcuts while retaining this data. Disable schedules in Settings before uninstalling, and remove any optional discovery hook registrations from the host application's configuration.

The ZIP still includes `Install.ps1` for the previous script-based installation flow. That script verifies and copies only files listed in the package manifest. It does not register an uninstaller; use the setup executable for the standard install experience.

Setup executables are currently unsigned. Code signing would require a separate signing certificate and release signing step. Checksums verify download integrity; they are not a substitute for a publisher signature.

## Smoke tests

After packaging:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-Package.ps1 -Version 1.0.0
```

The test extracts the actual ZIP, verifies its manifest, and removes system Git/.NET locations from the child-process environment. It creates an isolated Git repository, linked worktree, and local LFS object store; preserves unfinished, untracked, and hydrated LFS files through a real encrypted backup and restore; runs the hidden runner; and renders the WPF desktop. Fixtures and backup data stay under `.artifacts/package-smoke`.

On a clean Windows runner, include `-Installer` to test the running-app guard, setup, the Start menu shortcut and uninstall registration, the installed application, reinstall preserving the catalog, and uninstall preserving the catalog and backup repository. This temporarily registers the app for the current user and cleans it up afterward. The test refuses to overwrite an existing installation or Start menu shortcut. Read-only GitHub CI runs both smoke tests through `Verify.ps1 -Installer`.

## Manual GitHub Releases

Start from a clean checkout of a reviewed commit on a clean Windows machine. Review the commit ID, upstream native-tool updates, and the dependency/security reports before building. `git status --porcelain` must be empty and the checkout must include complete history. Select the next release version; do not replace existing published release assets to distribute a code change.

```powershell
git status --porcelain
git rev-parse HEAD
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Verify.ps1 -Installer -Version 1.0.1
if ($LASTEXITCODE -ne 0) { throw 'Release validation failed.' }
Get-Content .\dist\SHA256SUMS
Get-FileHash .\dist\RepoBackup-1.0.1-win-x64-setup.exe, .\dist\RepoBackup-1.0.1-win-x64.zip -Algorithm SHA256
```

The example version is only an example; use the version selected for the reviewed release. The entrypoint scans, audits, runs acceptance and compatibility tests, builds the installer and ZIP, generates checksums, and smoke-tests both packages. Confirm the Git checkout is still clean afterward and the hashes match `SHA256SUMS`. Keep the commit ID and local validation reports with your release records.

On GitHub's Releases page, create a draft with a new tag such as `v1.0.1` targeting that exact reviewed commit. Upload these three locally validated assets manually, review the release notes and unsigned-installer disclosure, then publish:

- `RepoBackup-<version>-win-x64-setup.exe`
- `RepoBackup-<version>-win-x64.zip`
- `SHA256SUMS`

The existing published release is retained. No release event triggers a publishing workflow. CI builds temporary packages only to test them and does not upload artifacts or publish assets; CI outputs never become releases automatically.

`.github/workflows/verify.yml` checks pull requests, pushes to `main`, and **Actions > Verify > Run workflow** on standard temporary Windows runners. Its token has only `contents: read` and checkout does not persist credentials. Runs use no repository secrets, stored artifacts, self-hosted runners, or paid runner types. See [Security maintenance](SECURITY.md) for dependencies and CI policy. Code signing remains deferred.

To verify a downloaded asset in PowerShell:

```powershell
Get-FileHash .\RepoBackup-1.0.0-win-x64-setup.exe -Algorithm SHA256
```

Compare the hash with the matching filename in `SHA256SUMS` downloaded from the same Release.
