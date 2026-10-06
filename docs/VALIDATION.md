# Validation and operating limits

The application is built on Windows with the pinned .NET SDK and restic release. The Release build treats warnings as errors. Tests use isolated SQLite catalogs, generated Git repositories and real restic repositories under `.artifacts`; they do not back up or modify user projects.

## Reproduce

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -Test -WindowsIntegration -Publish
```

Use `--unit` on the test executable for catalog, discovery, CLI and native hook checks. Use `--edge-only` for those checks plus link, low-space, live-change and external Git-data tests. Build the whole solution first so the CLI and hidden runner fixtures are available. Junction creation requires an environment permitting Windows reparse points.

## Acceptance coverage

- Discovery: three providers discovering one folder with one review decision, case and separator differences, percent-encoded drive letters and Unicode, UNC URI parsing, junctions, unavailable aliases, distinct clones/nested folders/worktrees, and overlapping workspaces without expanding existing roots.
- Provider isolation: fixture configuration for every provider; malformed and inaccessible input, corrupt read-only SQLite registries, metadata-only Claude session indexes, JSONC workspace files, recent-folder/file/remote distinctions, and a bounded decoder for sidebar workspace records. Provider failures preserve existing entries and other readers continue.
- Storage: serialized registration across independent catalog instances, provider-scoped ID collisions, stable dismissed/approved state, stale GUI saves, relinking/import ownership checks, transaction rollback, version 1 SQLite migration and portable imports with unchanged selections/history, version 2 roundtrips, secrets excluded, Windows DPAPI, and process-aware interruption recovery.
- CLI and hooks: all-source default, provider overrides, legacy Codex-only state/default hook commands, unrelated-handler preservation, one configuration backup, idempotent installs, configured versus verified status, invalid input and busy-database failure, and real Windows PowerShell commands forwarding Unicode JSON stdin to the hidden runner. Callbacks perform registration only, never enrollment, backups or interrupted-job recovery, and emit no context or permission output.
- Backup: actual restic snapshots, source/exclusion previews, protected tracked dependencies, `.env`, assets, `build`/`dist`, ignored/untracked files and embedded recovery manifests.
- Git recovery: commits, branches, staged and unstaged changes, two linked worktrees, local submodules, external submodule databases, external LFS storage and shared object alternates. Original file hashes remain unchanged.
- Restore: full recovery, folder selections, individual files, matching file hashes, new-directory requirements and recovery with only the destination plus key.
- Retention: eleven successful full backups retain ten; partial selections and failed/incomplete runs preserve full recovery points.
- Failures: unavailable source/destination, locked files, observed changes, cancellation, insufficient-space fault injection, separate backup/verification/cleanup outcomes and destination-operation collisions.
- Windows integration: hidden scheduled runner, GUI closed, disabled default, no overlapping task instances, missed-run catch-up XML, and an actual temporary Task Scheduler run. The fixture task is removed afterward.
- GUI: all six screens are rendered from the real WPF visual tree using a fixture catalog and checked for binding errors. The dashboard uses the supplied visual reference.
- Packaging: Windows x64 self-contained GUI, CLI and runner, pinned executable verification and package hashes.

## Hardware checks

Elevated VSS, a physical removable-drive disconnect during I/O, a genuinely full disk and a live UNC share require testing on the intended devices. Low space and unavailable destinations are covered through fault injection and missing paths. UNC destinations use the same repository and share-lock mechanisms, but network credentials are supplied by the signed-in Windows account.

Junction backup is tested without traversing the external target. Windows link restoration may require Developer Mode or elevation. UNC URI identity is tested without contacting a live share. The installer targets the signed-in user's stable application directory; scheduled backups run while that user is signed in and the GUI is closed.

Hook tests invoke the documented payload contracts through actual PowerShell and runner processes using fixture settings. Enabling and reviewing hooks inside the native programs is optional and requires user configuration; it is not performed by the suite. A real callback verifies installation. Antigravity's named PostInvocation handler requires a build supporting native Hooks and does not assume asynchronous-handler support.

Live coverage detects observed metadata changes and read failures; it cannot promise every file represents one instant. VSS is explicitly selected, requires Administrator rights, and does not silently fall back to live mode.

## Verified run: October 6, 2026

The Release build completed with zero warnings and zero errors. The complete acceptance suite, including native PowerShell/runner callbacks and the temporary Windows scheduled task, completed with **76 passed, 0 failed**. Projects, Discoveries and Settings rendered from the real WPF visual tree with no binding errors. A read-only discovery smoke test against installed program metadata found all three providers and enrolled no projects; an unavailable Antigravity workspace reference produced the expected warning.

Native hook configuration was not installed into user settings during validation. Actual agent callbacks remain an opt-in verification step after installation and native review.
