# Recovering projects with Repo Backup

Keep your backup repository safe. **Password-free repositories require only the backup folder to restore**, including on a replacement computer; anyone who can access the folder can restore its contents. **Protected repositories also require the recovery key**: export it and keep it somewhere safe, separate from the backup drive. Losing all copies of that key makes the protected backup unrecoverable.

Every repository contains file data, Git history and a recovery manifest for every snapshot. A catalog export is useful but is not required to restore. Restic uses its encrypted storage format in both modes; password-free means it can be unlocked without a secret. Existing repositories retain their protection when upgrading or opening them.

Catalogs migrate transactionally to version 4 on opening. Version 4 adds canonical path identities and legacy project/root identifiers; earlier selection pattern bases, retired destinations, program bindings, and source aliases remain preserved. Version 1–3 catalog imports and recovery manifests remain readable. Older app versions reject version 4 catalogs and manifests; use the updated app for new backups.

## On the original computer

1. Open **Snapshots & Restore**, select the destination, and refresh snapshots.
2. Choose a successful full-project recovery point, or a partial selection if that is what you need.
3. Choose **Restore entire recovery point**, **Restore selected file / folder**, or **Test restore**. Select a parent folder. The app creates a new child directory and verifies restored file data.
4. Open the recovered project under `sources`. Each source root and worktree has its own numbered folder. `recovery-manifest.json` records the original and restored mappings.

All three restore actions show progress across the requested sources, the restored size, and the latest restored filename. Hover over a shortened filename to see its full path. Preparation, verification, and finalization show animated progress; 100% appears only after the restore finishes successfully. Cancel remains available while the restore is running.

Incomplete snapshots can contain useful files. Their status remains visible and they do not count as successful protection. A failed restore leaves `RESTORE-INCOMPLETE.txt` in its new directory; retry into another new directory.

Incremental backups are complete recovery points: restore one selected snapshot normally, without replaying earlier backups. Source read mode is independent of full-project versus partial-selection coverage. Multi-project backups stay pending until the shared repository check passes. Cancellation before that check leaves the new snapshots pending and preserves earlier successful versions. Failure to reclaim unused data does not invalidate an already verified recovery point.

On Windows, incremental reads reuse file contents when path, size and modification time match. Edits that preserve size and modification time may be missed until a full source reread. The first backup after seven days performs that reread automatically; **Settings → Reread all source files on the next manual backup** or CLI `backup --read-mode full` requests it sooner. **Verify Repository** checks stored data and does not compare current sources with the backup.

## On a replacement computer

1. Install the Windows x64 package. It includes private Git and Git LFS tools for reconstructing worktrees and submodules.
2. In **Destinations**, choose **Add / open repository**, select **Open an existing restic repository**, and select the backup folder. Leave **No password required** selected for password-free backups. For a protected backup, choose **Protect with a recovery key** and load its exported key.
3. Choose **Rebuild catalog from snapshots**. Projects return disabled, retaining their original identities, selections and history.
4. Browse snapshots and restore into a new folder. Original source paths may be unavailable; recovery does not depend on them.
5. Relink moved source roots and enable the projects you want to protect going forward. Configure scheduling separately.

Git recovery rewrites `.git` files, worktree `gitdir` and `commondir` pointers, object alternates, submodule working-directory settings and local LFS storage paths before running repair. Restored administrative files are made writable where Git for Windows requires this. External config includes that cannot be mapped are commented out. Original repositories are never modified. A missing Git dependency causes a visible recovery error.

Git commands discard inherited repository, index, object-store and injected configuration environment variables. Recovery checks the Git directory, common directory and working tree against its restored source mappings before running repair. Nested Git databases are recorded separately from their storage mappings and remain included even when stored in an excluded folder such as `obj`.

## Worktree relocation and stale registrations

Version 4 manifests record the resolved Git directories, common databases, worktrees, and unavailable registrations. Backup inspection resolves a moved worktree only when the captured registration is unambiguous, points back to that worktree, and belongs to the expected common repository. Inspection uses explicit Git directories and never repairs the source repository.

Full restore reconstructs available worktrees inside the new recovery directory. Foreign and confirmed stale registrations are removed from the active restored database and preserved under `git-registration-archive`, with their original pointers and explanation. Detached worktree HEADs receive recovery references under `refs/repobackup/orphan-worktrees` so available commits remain reachable. No unavailable working files can be recreated. Version 1–3 snapshots use their captured reciprocal pointers to infer these relationships; ambiguous or missing required dependencies produce a specific recovery error. Recovery does not consult the original repository to repair missing captured data.

## Canonical project identities

Catalog version 4 treats ordinary and extended drive/UNC paths as one identity. Before migration, the app saves `catalog.db.pre-v4-<timestamp>-<id>.db` beside the catalog using SQLite's backup API, including committed WAL data. Migration is transactional. Equivalent projects with matching exclusions combine history and preserve legacy IDs in portable exports; an unused discovery-only subset can be absorbed without treating its smaller backup as full coverage. Existing historical outcomes remain unchanged.

Projects with conflicting exclusions remain visible for explicit resolution. Select the project whose rules you want, choose **Resolve duplicate settings**, and review the confirmation. The CLI equivalent is `resolve-duplicates --project <survivor-id>`. This applies only to identical full root sets. Retention combines equivalent full-project histories only after a new verified successful backup. Saved selections retain independent series.

## Empty folders and selection rules

Version 3 and later manifests preserve source folders and nested folders that become empty after exclusions. Repo Backup recreates those folders without copying excluded files. Snapshot browsing and selective restore include these folders. Standalone restic does not recreate directories stored only in the manifest.

An older snapshot may declare a source whose contents were never stored. Full restore reports the missing source before creating a target; select an available file or source folder to recover its data. Omitted Git metadata and files cannot be recovered retroactively. Create a new backup after upgrading.

New GUI and CLI folder selections inherit project exclusions relative to each original project root. Older selections keep their existing selection-relative pattern meaning. The exclusion editor identifies the base; **Use project exclusions** explicitly copies the project's rules and changes the selection to project-relative patterns. Expanding or retrying the selection tree preserves missing and unreadable saved paths until you explicitly uncheck them. Error rows show unresolved entries and offer a retry.

## Replacing a repository at a saved path

Normal operations reject a different repository at a saved destination path. To register an intentional replacement, use **Add / open repository → Open an existing restic repository**, or `destination-open`, with the replacement's protection mode and key if required.

Opening the same repository preserves its destination identity. A different repository gets a new identity and separate protection history. The previous destination is marked **replaced — history only**; its history and recovery key remain available. Its catalog schedules and existing Windows tasks are disabled. In Settings, choose an active destination and explicitly enable and save each schedule to resume backups. A failed repository validation leaves the previous registration and credentials intact.

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

For password-free backups, omit `--key-file` from `destination-open`. No files or credentials from the old computer are needed. To create a new protected backup, use `destination-add --protection recovery-key`, then export its key with `key-export --destination <destination-id> --output <new-key-file>`.

The standalone pinned restic executable in `restic\restic.exe` can also recover ordinary files using normal restic commands. Supply your exported password file for protected repositories, or pass `--insecure-no-password` on every command for password-free repositories. Snapshot tags identify their stable series, full/partial coverage, success status and manifest path. Use Repo Backup's CLI or desktop app when Git path reconstruction or WSL symbolic-link restoration is needed. Standalone restic restores the manifest containing WSL links but does not recreate those links itself.

## WSL/Linux symbolic links

Repo Backup captures version 1 and version 2 `IO_REPARSE_TAG_LX_SYMLINK` entries directly through Windows APIs, without requiring a running WSL distribution. Their raw link data and timestamps are stored in version 3 and later recovery manifests alongside directory and Git metadata. Existing version 1–3 manifests remain readable. Older app versions reject unsupported manifest versions rather than silently omit their metadata.

Full, folder, and individual-link restores recreate and verify the link itself after ordinary files and Git relationships have been restored. The destination must support Windows reparse points and permit their creation; unsupported filesystems or insufficient permissions produce a failed restore with `RESTORE-INCOMPLETE.txt`. Retry into a new directory on a supported volume with suitable permissions. A local NTFS directory supports these link entries without needing to start WSL.

Targets are preserved exactly, including absolute Linux paths, relative paths, and dangling or cyclic links. External targets are never traversed or copied. Absolute targets are not relocated and can remain dangling on another computer. Unknown link versions, malformed metadata, and unreadable entries still produce incomplete coverage. Snapshot browsing and the CLI `files` command include links stored in the manifest, including folders whose only contents are links.

Manifest metadata is captured separately from restic's VSS view. Both live and VSS backups recheck WSL link fingerprints, timestamps, recorded directories and Git metadata locations before completing; observed changes prevent successful status and retention cleanup. This check does not provide an atomic VSS snapshot of manifest metadata. Links skipped by earlier snapshots are not recoverable from those snapshots.

## Coverage and verification

Live backups capture files while applications may be changing them. Observed changes, missing drives, unreadable files, cloud placeholders or unresolved Git data produce incomplete coverage. They never trigger retention cleanup for their series. Hydrate OneDrive files locally before retrying. Missing folders, unavailable drives, and access failures include the affected path and relink guidance; other queued projects continue. A stable warning is not reported as a file change. Actual differences include added, removed, modified, and metadata-changed counts and up to 100 representative paths. Repeated scan messages are deduplicated.

VSS backups also recheck the prepared exact-file selection. Added or removed paths and file-type changes keep coverage incomplete, including newly created Git objects; ordinary content edits are read from the shadow copy. VSS is an explicit manual mode that requires running as Administrator. VSS errors do not silently become successful live backups. Windows symbolic-link restoration may require Developer Mode or elevation. Recovery tests link creation on the target volume before transferring project contents and reports an affected snapshot path if Windows rejects it. Retry as Administrator or with Developer Mode enabled into a new directory. Links are preserved and verified without traversing external or missing targets.

Keep the recovery folder closed in Git clients and other tools until `RESTORED.txt` appears. If native readback finds up to 100 specific regular files with incorrect contents or sizes, recovery makes one verified retry in a private directory, then atomically replaces only those output files. It does not write through output hard links to original files. Successful repairs are recorded as structured advisories in `restore-verification-repairs.json` and noted in `RESTORED.txt`. An unsuccessful retry, access error, repository error, or unknown failure leaves recovery incomplete. Snapshot paths that collide under Windows case comparison are rejected before extraction.

Every successful backup passes a repository structural check before retention keeps the newest ten successful snapshots **in its own series**. **Verify repository** additionally reads and checks all stored data. **Test restore** reconstructs files in a new directory and verifies their data. Backup, verification and cleanup outcomes are recorded separately.

## Interrupted repository operations

Repository lock failures are retried for a bounded period and remain cancellable. Recovery validates the repository identity before changing lock metadata. It uses normal restic stale-lock cleanup, with an additional Windows check for locks whose owning process has exited but still has an open process handle. That check requires the current Windows account and host, a confirmed exited owner, an authenticated lock record, a matching content hash, and a regular lock file inside the repository. Live owners and unknown dependencies remain intact. Recovery never uses `unlock --remove-all`.

## References

- [Restic backup](https://restic.readthedocs.io/en/v0.18.0/040_backup.html)
- [Restic recovery](https://restic.readthedocs.io/en/v0.18.0/050_restore.html)
- [Restic snapshot retention](https://restic.readthedocs.io/en/v0.18.0/060_forget.html)
- [Git worktree recovery](https://git-scm.com/docs/git-worktree)

## Backup status and recovered catalog metadata

Project protection and **Needs backup** use full-project history at the currently selected destination. A project needs backup when there is no verified full backup there, or a later full attempt was unsuccessful. This status does not detect changed files. Partial selections do not establish full-project protection. A cleanup failure stays visible while retaining its verified recovery point.

Catalog rebuilding chooses the newest valid manifests for missing identities and selections, maps older selection paths through stable root identities to the recovered project's latest roots, and applies the recovered metadata in one transaction. Existing local projects, selections, and job outcomes are preserved; recovered projects remain disabled until reviewed. Exact selective restores validate the requested path against snapshot contents and verify the restored contents before reporting success.
