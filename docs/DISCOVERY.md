# Project discovery and optional hooks

Startup, **Refresh**, and every backup refresh saved project metadata from Codex, Claude Code and Antigravity. Discovery adds review candidates and program labels; it never deletes entries, enables backups or clears a dismissal. Installations that have no saved state are skipped. Invalid or inaccessible input produces a program-specific warning while the other readers continue.

## Folder identity and workspace groups

The catalog matches normalized Windows paths without regard to case, resolving symbolic links and junctions when accessible. It also remembers observed aliases and previous relinked paths so an unavailable drive or broken alias does not recreate a project. Names and Git remotes are not identity keys. Separate clones, linked worktrees and nested folders stay separate unless they were explicitly registered as roots of the same project.

When a saved workspace spans existing project entries, each keeps its roots, name, IDs, exclusions, selections, review decision and backup history. All matching entries receive the program binding. Unmatched folders form one new review candidate; existing roots are never silently enlarged. Matching and writing take place in one SQLite write transaction shared by the GUI, scanner, CLI and hidden runner. Manual registration uses this registrar; conflicting relinks and imports are rejected atomically.

Catalog version 2 removes global external-ID uniqueness. Bindings are scoped by provider and may reference multiple project entries, but matching always uses folders. Version 1 databases migrate transactionally, and version 1 portable imports and older recovery manifests still work.

## Sources and overrides

| Program | Default sources | CLI overrides |
| --- | --- | --- |
| Codex | `CODEX_HOME` or `~/.codex`, `.codex-global-state.json` saved projects | `--codex-state <file>` |
| Claude Code | `~/.claude.json` project paths and `~/.claude/projects/*/sessions-index.json` original/project paths | `--claude-config <file>`, `--claude-projects <directory>` |
| Antigravity | `%APPDATA%/Antigravity/User/globalStorage/state.vscdb` workspace keys and `User/workspaceStorage/*/workspace.json` | `--antigravity-user-data <directory>` (application data directory or its `User` directory) |

`CLAUDE_CONFIG_DIR` changes Claude's config to `<directory>/.claude.json` and its index/settings directory to that directory. An explicit `--claude-config` outside the default location uses the config file's adjacent `projects` directory unless `--claude-projects` is supplied. Session directory slugs and transcript contents are never decoded or read for project discovery. [Claude Code local storage](https://code.claude.com/docs/en/claude-directory)

Antigravity's database is opened read-only and only known workspace registry keys are selected: recent workspace history, the encoded sidebar workspace map and the legacy workspace cascade map. The sidebar decoder reads URI map keys and skips opaque row data and unknown fields. Workspace-storage metadata provides an independent fallback. Referenced workspace files support JSON comments, trailing commas, paths relative to the workspace file, percent-encoded local file URIs and UNC file URIs. Individual-file history and remote workspace URIs are ignored.

`discover` scans all programs by default. Choose `--source codex|claude-code|antigravity|all` to limit it. The compatibility flag `discover --state <file>` always means Codex only; combine the new `--codex-state` flag with `--source all` for mixed discovery. A command's overrides apply to that discovery call. Associated programs are displayed and searchable in **Projects** and **Discoveries**.

Only folder paths, aliases, provider IDs, external project IDs and project labels enter the catalog. Raw conversations, credentials, configuration bodies and hook payloads are not persisted. Warnings contain generic failure categories, not source exception messages or contents.

## Optional live registration

Install the packaged app into its stable per-user directory first. **Settings** has separate opt-in controls for the three programs. Installation preserves unrelated settings and handlers, backs up an existing configuration before replacement, and writes atomically. Reinstalling the same configuration and catalog does not duplicate the handler or reset verification status.

| Program | Optional handler and configuration | Native review |
| --- | --- | --- |
| Codex | Asynchronous `SessionStart` in `CODEX_HOME/hooks.json`, registers `cwd` | Review/trust through `/hooks`, then start or resume a session |
| Claude Code | Asynchronous `SessionStart` in `~/.claude/settings.json` (or `CLAUDE_CONFIG_DIR/settings.json`), registers `cwd` | Review through `/hooks`, then start or resume a session |
| Antigravity | Named `repo-backup-discovery` with `PostInvocation` in `~/.gemini/config/hooks.json`, registers `workspacePaths` | Requires a build with native Hooks support; review **Customizations > Hooks**, then send a prompt |

The Antigravity handler uses the documented named format without an `async` property. An unrelated handler already using `repo-backup-discovery` is preserved and reported as a conflict. [Claude Code hooks](https://code.claude.com/docs/en/hooks), [Antigravity hooks](https://antigravity.google/docs/hooks)

The shared command starts the stable hidden runner with the selected catalog directory, explicit UTF-8 stdin pipes and a bounded process wait. Registration has a short catalog lock timeout. Failures are quiet and write only a bounded error category locally. Hooks emit no context or permission decisions and perform no backup, enrollment or interrupted-job recovery.

Status reads **Configured · awaiting first successful event** until a callback succeeds. Press **Refresh** to update status after the native event. Installation alone does not imply the program has invoked its hook.

```powershell
.\RepoBackup.Cli.exe install-hook --source claude-code --data-dir C:\Catalog
.\RepoBackup.Cli.exe install-hook --source antigravity --data-dir C:\Catalog
.\RepoBackup.Cli.exe hook-status --data-dir C:\Catalog
```

`install-hook` also accepts `--config <file>`. Existing `install-hook` and `register-hook` commands without `--source` retain their Codex defaults. `register-hook` reads JSON from stdin and exits quietly even when registration fails.
