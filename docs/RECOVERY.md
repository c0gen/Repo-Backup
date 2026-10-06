# Recovering projects with Repo Backup

Keep the backup repository and its exported recovery key in separate locations. The repository contains encrypted file data, Git history and a recovery manifest for every snapshot. A catalog export is useful but is not required to restore.

Catalogs migrate transactionally to version 2 on opening. Version 2 exports retain program bindings and observed source aliases; version 1 imports and existing recovery manifests remain supported. The migration preserves project/root identities, saved selections and backup history. Program identifiers do not determine folder identity or change backup retention series.

## On the original computer

1. Open **Snapshots & Restore**, select the destination, and refresh snapshots.
2. Choose a successful full-project recovery point, or a partial selection if that is what you need.
3. Choose **Restore entire recovery point**, **Restore selected file / folder**, or **Test restore**. Select a parent folder. The app creates a new child directory and verifies restored file data.
4. Open the recovered project under `sources`. Each source root and worktree has its own numbered folder. `recovery-manifest.json` records the original and restored mappings.

Incomplete snapshots can contain useful files. Their status remains visible and they do not count as successful protection. A failed restore leaves `RESTORE-INCOMPLETE.txt` in its new directory; retry into another new directory.

## On a replacement computer

1. Install the Windows x64 package. Git for Windows is required to reconstruct Git worktrees and submodules.
2. In **Destinations**, choose **Add / open repository**, select **Open an existing restic repository**, and load its recovery key.
3. Choose **Rebuild catalog from snapshots**. Projects return disabled, retaining their original identities, selections and history.
4. Browse snapshots and restore into a new folder. Original source paths may be unavailable; recovery does not depend on them.
5. Relink moved source roots and enable the projects you want to protect going forward. Configure scheduling separately.

Git recovery rewrites `.git` files, worktree `gitdir` and `commondir` pointers, object alternates, submodule working-directory settings and local LFS storage paths before running repair. Restored administrative files are made writable where Git for Windows requires this. External config includes that cannot be mapped are commented out. Original repositories are never modified. A missing Git dependency causes a visible recovery error.

## Without the GUI

```powershell
$cli = "$env:LOCALAPPDATA\Programs\RepoBackup\RepoBackup.Cli.exe"
& $cli destination-open --name Recovered --path E:\RepoBackups --key-file D:\Safe\recovery.key
& $cli destinations
& $cli recover --destination <destination-id>
& $cli snapshots --destination <destination-id>
& $cli restore --destination <destination-id> --snapshot <snapshot-id> --target C:\Recovered\NewProject
& $cli verify --destination <destination-id>
```

The standalone pinned restic executable in `restic\restic.exe` can also recover files using normal restic commands and your exported password file. Snapshot tags identify their stable series, full/partial coverage, success status and manifest path. Repo Backup's CLI should be used when Git path reconstruction is needed.

## Coverage and verification

Live backups capture files while applications may be changing them. Observed changes, missing drives, unreadable files, cloud placeholders or unresolved Git data produce incomplete coverage. They never trigger retention cleanup. Hydrate OneDrive files locally before retrying.

VSS is an explicit manual mode that requires running as Administrator. VSS errors do not silently become successful live backups. Windows symbolic-link restoration may require Developer Mode or elevation. Links are preserved without traversing external targets.

Every successful backup passes a repository structural check before retention keeps the newest ten successful snapshots **in its own series**. **Verify repository** additionally reads and checks all stored data. **Test restore** reconstructs files in a new directory and verifies their data. Backup, verification and cleanup outcomes are recorded separately.

## References

- [Restic backup](https://restic.readthedocs.io/en/v0.18.0/040_backup.html)
- [Restic recovery](https://restic.readthedocs.io/en/v0.18.0/050_restore.html)
- [Restic snapshot retention](https://restic.readthedocs.io/en/v0.18.0/060_forget.html)
- [Git worktree recovery](https://git-scm.com/docs/git-worktree)
