# Repo Backup: Windows GUI for project backups

## Summary

Build a Windows desktop application that discovers Codex projects, lets you choose repos or folders, and creates recoverable, versioned backups to a drive or network folder.

Your selected defaults:

- **Manual backups**, with scheduling available inside the app.
- **Review newly discovered projects** before including them.
- Include Git history, unfinished changes, configuration, assets, and associated worktrees.
- Exclude known dependencies and caches.
- Keep the **latest ten successful versions per project or folder selection**.

Use **restic as the backup engine**, with a custom GUI and project-discovery layer. Restic already provides snapshots and deduplication, allowing development to focus on project selection and reliable recovery. [Restic documentation](https://restic.readthedocs.io/en/stable/040_backup.html)

## Architecture and project discovery

- Build using C#/.NET 10 and WPF. Package a self-contained Windows x64 application with a pinned, checksum-verified restic executable. Provide a per-user installation with a stable executable path for scheduling.
- Separate the GUI, headless CLI, catalog, discovery, backup engine, Git recovery, and Windows scheduling modules. The GUI and CLI share the same application services.
- Store the authoritative catalog and job history in SQLite under `%LOCALAPPDATA%\RepoBackup`. Provide versioned JSON import/export for portability. SQLite handles concurrent GUI, hook, and scheduler access more reliably than independently rewriting a JSON manifest.
- Model projects with stable IDs and multiple source roots, plus saved selections, destinations, exclusion rules, and backup history. Relinking a moved folder preserves its identity and history.
- Import Codex’s saved projects through an isolated, read-only adapter for its local state file. Support projects with multiple roots, including roots on different drives. Treat this internal format as changeable: parsing failures must preserve the existing catalog and leave manual registration operational.
- Refresh discovery on application startup, before backups, and through a **Refresh** button. Also support adding folders manually and scanning user-selected locations for Git repositories.
- Put discoveries in a review inbox. Show broad locations such as drive roots and Downloads explicitly; discovering a location never enrolls it automatically. Missing or inaccessible paths remain listed for relinking or disabling.
- Offer an optional asynchronous Codex `SessionStart` hook that registers the working directory through the CLI. It records candidates only and never starts backups. Preserve existing hooks, use Codex’s normal trust flow, and keep discovery functional without the hook. No agent-maintained global instructions are required. [Codex hooks](https://learn.chatgpt.com/docs/hooks)

## GUI and backup behavior

- **Projects:** searchable list with checkboxes, source paths, worktrees, availability, last successful backup, and status. Main actions: **Back Up All Enabled**, **Back Up Selected**, **Add Folder**, and **Refresh**.
- **Selection and preview:** expandable folder selection, saved selections, exclusion editing, file counts, estimated source size, and destination free space. Clearly distinguish full-project coverage from partial-folder coverage.
- **Destinations:** save local/external-drive and UNC network-folder destinations; choose one per run. Validate the backup repository’s identity and reject destinations inside selected source trees.
- **Default contents:** include `.git`, staged and unstaged changes, untracked files, ignored configuration such as `.env`, assets, and local Git LFS/submodule data. Do not use `.gitignore` as the backup exclusion list.
- Exclude recognized dependency/cache directories such as `node_modules`, `.venv`, and `__pycache__`, while preserving tracked files. Show exclusions in the preview and allow overrides. Keep generic `build` and `dist` folders unless explicitly excluded.
- Full-repo backups discover associated worktrees and include their working files, shared Git database, and individual Git metadata. Partial-folder backups remain a separate backup series and do not update the repo’s “fully backed up” status.
- Use one snapshot per project or saved selection, sharing a restic repository for deduplication. Store source mappings and recovery metadata with the snapshots so recovery does not depend on the original computer’s catalog.
- Serialize destination operations and show progress, cancellation, and per-project results. Interrupted runs remain visibly interrupted after restarting the app.
- After a successful backup and repository check, retain the latest **ten successful snapshots in that series**, then reclaim unused data. Group by stable project/selection identity so partial backups cannot displace full backups. Failed or incomplete runs never trigger retention cleanup. [Restic retention](https://restic.readthedocs.io/en/stable/060_forget.html)
- Store the repository password using Windows user-protected storage and provide an exportable recovery key. Keep credentials out of JSON exports and logs.
- **Scheduling:** disabled initially. Settings allow daily, weekly, or interval schedules for all enabled projects or a saved selection. Use Windows Task Scheduler with a hidden runner, no overlapping jobs, and missed-run catch-up. Run under the signed-in user; the GUI need not remain open.

## Restore and reliability

- Provide a snapshot browser with project, date, coverage, and success status. Restore an entire project, folder, or individual file into a **new destination directory**.
- Restore worktrees with their recorded path mappings. Rewrite restored Git references to remain within the recovery directory before repairing connections; never modify the original repository. Validate reconstructed worktree relationships. [Git worktree recovery](https://git-scm.com/docs/git-worktree)
- Allow opening an existing backup repository with its recovery key and rebuilding the catalog on a replacement computer.
- Report unavailable drives, inaccessible files, unresolved Git dependencies, and unreadable OneDrive placeholders as incomplete coverage—not successful protection.
- Preserve links without automatically traversing their external targets. Detect overlapping roots and prevent backup-destination recursion.
- Default to standard-user live backups; detect and report observed file changes or locks. Offer an explicit elevated VSS mode for Windows volume snapshots, with failures reported instead of silently falling back. Live mode does not promise a single instant across all files. [Windows VSS support](https://restic.readthedocs.io/en/stable/040_backup.html)
- Provide **Verify Repository** and a test-restore action. Keep backup, verification, and cleanup results separately visible.

## Implementation sequence and acceptance tests

1. **Core and catalog:** implement SQLite storage, JSON import/export, discovery adapters, and CLI commands for registration, discovery, backup, restore, and verification.
2. **Backup and recovery:** implement restic integration, selection rules, worktree metadata, recoverable snapshots, and ten-version retention.
3. **Desktop application:** add the project list, discovery inbox, preview, progress reporting, and restore browser.
4. **Windows integration:** add credential storage, in-app scheduling, optional Codex hook setup, packaging, and recovery instructions.

Acceptance requires:

- Repeated discovery creates no duplicates, preserves selections, and handles multiple roots, moved folders, unavailable drives, and changed Codex state formats.
- Restoring a fixture repo preserves commits, branches, staged/unstaged changes, untracked configuration, and multiple worktrees, with no writes to the original repo.
- File hashes match after full and selective restores; exclusion overrides behave as previewed.
- The eleventh successful backup leaves ten versions in its series; failures and partial selections cannot remove full-project recovery points.
- Disconnects, insufficient space, cancellation, application crashes, and concurrent GUI/scheduler requests produce accurate status and preserve earlier backups.
- A fresh installation can recover using only the backup destination and recovery key.
- Scheduling remains off until configured and works while the GUI is closed.

Initial scope is one Windows user, local/UNC destinations, and versioned file/project recovery. Cloud destinations, bidirectional synchronization, and whole-machine recovery are deferred.
