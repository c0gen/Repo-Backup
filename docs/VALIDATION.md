# Validation and operating limits

The application is built on Windows with the pinned .NET SDK and restic release. The Release build treats warnings as errors. Tests use isolated SQLite catalogs, generated Git repositories and real restic repositories under `.artifacts`; they do not back up or modify user projects.

## Reproduce

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -Test -WindowsIntegration -Publish
```

Use `--unit` on the test executable for catalog, discovery, CLI and native hook checks. Use `--edge-only` for those checks plus link, low-space, live-change and external Git-data tests. Build the whole solution first so the CLI and hidden runner fixtures are available. Junction creation requires an environment permitting Windows reparse points.

Use `--destinations-only` for desktop workflow, protection/export, and core/CLI destination checks without repeating the full backup and retention suites. Combine it with `--windows-integration` to include temporary schedule creation, repeated editing, and missing-task removal. Use `--reliability-only` for exact restore, cancellation/finalization, moved-root recovery, and reactivation regressions.

Build release installers with `scripts/Build.ps1 -Test -Installer`. Run `scripts/Test-Package.ps1` to verify the actual ZIP and packaged backup/restore, hidden runner, and desktop with system Git/.NET discovery disabled. Include `-Installer` on a clean Windows runner to check setup, reinstall, uninstall, and preserved user data. See [Packaging and releases](PACKAGING.md) for commands and CI behavior.

## Acceptance coverage

- Discovery: three providers discovering one folder with one review decision, case and separator differences, percent-encoded drive letters and Unicode, UNC URI parsing, junctions, unavailable aliases, distinct clones/nested folders/worktrees, and overlapping workspaces without expanding existing roots.
- Provider isolation: fixture configuration for every provider; malformed and inaccessible input, corrupt read-only SQLite registries, metadata-only Claude session indexes, JSONC workspace files, recent-folder/file/remote distinctions, and a bounded decoder for sidebar workspace records. Codex fixtures cover extension-only thread metadata, named workspace roots, numeric database versions, committed WAL records, registry override compatibility and subagent exclusion. VS Code fixtures cover Stable/Insiders/profile metadata, ordinary workspaces, extension ownership keys, family labels, portable overrides and refreshes. Copilot fixtures cover first-record-only parsing, empty windows, large transcripts, the 1 MiB header limit and unknown producers. Provider failures preserve existing entries and other readers continue; source hashes and catalog exports verify metadata-only, read-only discovery.
- Antigravity history: missing workspace files on available volumes are skipped; offline volumes and locked or malformed workspace files still warn. Valid discoveries continue and saved projects and provider files remain intact.
- Storage: serialized registration across independent catalog instances, provider-scoped ID collisions, stable dismissed/approved state, stale GUI saves, relinking/import ownership checks, transaction rollback, version 1 SQLite migration and portable imports with unchanged selections/history, version 2 roundtrips, secrets excluded, Windows DPAPI, and process-aware interruption recovery.
- CLI and hooks: five-source default, VS Code/Copilot filters and Codex-home/provider overrides, legacy Codex-only state/default hook commands, rejection of native hooks for discovery-only sources, unrelated-handler preservation, one configuration backup, idempotent installs, configured versus verified status, invalid input and busy-database failure, and real Windows PowerShell commands forwarding Unicode JSON stdin to the hidden runner. Callbacks perform registration only, never enrollment, backups or interrupted-job recovery, and emit no context or permission output.
- Backup: actual restic snapshots, source/exclusion previews, protected tracked dependencies, `.env`, assets, `build`/`dist`, ignored/untracked files and embedded recovery manifests.
- Destination protection: password-free and recovery-key repositories, protected defaults for legacy imports, credential-free password-free operations, inherited password isolation, failed-open rollback, atomic recovery-key export, CLI defaults, key-file inference and conflicting-option rejection.
- Git recovery: commits, branches, staged and unstaged changes, two linked worktrees, local submodules, external submodule databases, external LFS storage and shared object alternates. Original file hashes remain unchanged.
- Restore: full recovery, folder selections, individual files, matching file hashes, new-directory requirements and replacement-computer recovery in both modes. The fixture copies only the repository, makes original source paths and credentials unavailable, rebuilds a fresh catalog and checks restored hashes and Git history. Protected recovery uses an exported key; password-free recovery uses none.
- Retention: eleven successful full backups retain ten; partial selections and failed/incomplete runs preserve full recovery points.
- Failures: unavailable source/destination, locked files, observed changes, cancellation, insufficient-space fault injection, separate backup/verification/cleanup outcomes and destination-operation collisions.
- Windows integration: hidden scheduled runner in both protection modes, GUI closed, disabled default, no overlapping task instances, missed-run catch-up XML, and actual temporary Task Scheduler runs. Fixture tasks are removed afterward.
- GUI: all six screens are rendered from the real WPF visual tree using a fixture catalog and checked for binding errors. The dashboard uses the supplied visual reference.
- Destination GUI: real WPF dialog controls and view-model commands check the password-free default, protected-warning cancellation, immediate export, skipped and later export, fresh-catalog opening, key-field visibility and key-export availability. Dialog and destination-page PNGs are generated under the test artifacts without showing desktop windows.
- Recovery/workflow regressions: bracketed file and folder names with similarly named siblings, nonexistent selections, individual-file series, and a restore process reporting success without restoring contents. Cancellation covers projects skipped before starting, verification/finalization, cleanup, and CLI/runner exit code 130; tagging failures cannot claim success. Cleanup errors preserve verified recovery points.
- Catalog reconstruction: newest valid project/selection metadata, older selections mapped to moved root identities, invalid newest metadata fallback, transaction rollback, and preservation of local project, selection and job edits. Recovered projects stay disabled.
- Desktop workflow: destination-specific full-project history and Needs backup, delayed snapshot/file responses after destination or snapshot changes, filtered selection toggling with hidden counts, dismissed-project reactivation, complete preview warnings, and persistent repository verification failures.
- Schedule editing: explicit New/Edit state, names, independent destinations and scope, relevant frequency fields, repeated saves updating one actual temporary Windows task, and removing imported schedules with no local task.
- Layout/accessibility: all six WPF pages render at 1140×730 and 1500×960, at 100% and 150% rendering scale. Checks verify visible primary actions and project status, tab access to the destination and both backup actions, and disabled destination controls during repository operations. PNGs are retained with the test artifacts.
- Packaging: Windows x64 self-contained GUI, CLI and runner, pinned executable verification and package hashes.

## Hardware checks

Elevated VSS, a physical removable-drive disconnect during I/O, a genuinely full disk and a live UNC share require testing on the intended devices. Low space and unavailable destinations are covered through fault injection and missing paths. UNC destinations use the same repository and share-lock mechanisms, but network credentials are supplied by the signed-in Windows account.

Junction backup is tested without traversing the external target. Windows link restoration may require Developer Mode or elevation. UNC URI identity is tested without contacting a live share. The installer targets the signed-in user's stable application directory; scheduled backups run while that user is signed in and the GUI is closed.

Hook tests invoke the documented payload contracts through actual PowerShell and runner processes using fixture settings. Enabling and reviewing hooks inside the native programs is optional and requires user configuration; it is not performed by the suite. A real callback verifies installation. Antigravity's named PostInvocation handler requires a build supporting native Hooks and does not assume asynchronous-handler support.

Live coverage detects observed metadata changes and read failures; it cannot promise every file represents one instant. VSS is explicitly selected, requires Administrator rights, and does not silently fall back to live mode.

## Earlier validation: October 6, 2026

The Release build completed with zero warnings and zero errors. The complete acceptance suite, including native PowerShell/runner callbacks and the temporary Windows scheduled task, completed with **76 passed, 0 failed**. Projects, Discoveries and Settings rendered from the real WPF visual tree with no binding errors. A read-only discovery smoke test against installed program metadata found all three providers and enrolled no projects; an unavailable Antigravity workspace reference produced the expected warning.

Native hook configuration was not installed into user settings during validation. Actual agent callbacks remain an opt-in verification step after installation and native review.

Following the VS Code/assistant-extension discovery update, the Release build again completed with zero warnings and zero errors. The expanded `--unit` suite completed with **68 passed, 0 failed**, including extension-only Codex metadata, shared desktop/database project IDs, committed WAL reads, VS Code Stable/Insiders/profiles, source attribution, bounded Copilot headers, CLI overrides, native hook compatibility and the isolated junction fixture. This subsequent run used synthetic metadata and did not install hooks into user settings or back up user projects.

## Bug and usability fix validation: October 6, 2026

The final Release build completed with **zero warnings and zero errors**. The complete acceptance suite with `--windows-integration` completed with **164 passed, 0 failed**. Focused runs also passed: **14 reliability regressions** and **25 desktop/destination checks**, including temporary Windows schedule creation, repeated saves, and deletion of imported schedules with no task. All fixture tasks were removed.

The full suite retained backup/restore, working-file hash, original-source integrity, retention, catalog migration, discovery/hook, external Git-data, and replacement-computer recovery checks. Exact bracketed file/folder and individual-file selection restores, missing/no-op restores, cancellations between projects and during finalization/cleanup, tagging failures, moved-root metadata, local edit preservation, invalid newest-manifest fallback, and transaction rollback passed.

All six desktop pages rendered at 1140×730 and 1500×960, with 100% and 150% rendering scale and no binding errors. Tab navigation reached the destination and both primary backup actions. Destination controls disabled during repository verification, and its failure remained visible after the information dialog returned. Two-destination protection, delayed snapshot/file responses, filtered toggling/hidden counts, full preview warnings, dismissed-project reactivation, and independent schedule destination/scope persistence passed.

Final artifacts: `.artifacts/tests/20261006-165410-77f10f/`. Full run log: `.artifacts/bugfix-acceptance-final.log`. Desktop PNGs are under the artifact directory's `desktop-destinations/workflow/` folder. Repository formats, recovery manifest version, catalog schema, and CLI command syntax remain unchanged.
