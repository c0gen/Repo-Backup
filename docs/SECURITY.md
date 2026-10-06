# Security validation and maintenance

## Local and CI checks

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Verify.ps1` from a complete Git checkout on Windows. Add `-Package` for portable packaging and smoke tests, or `-Installer` on a clean machine for both packaging formats and installer smoke tests. The same entrypoint runs in GitHub CI. All reports and fixtures remain local under ignored `.artifacts/verification/` directories; CI does not upload them.

Validation scans all fetched Git history and current tracked/non-ignored source with pinned Gitleaks 8.30.1. Console and JSON findings are fully redacted. Shallow checkouts fail validation; use `git fetch --unshallow` first. Downloads and the extracted scanner executable are checked against pinned SHA-256 hashes. The scanner uses its complete built-in rules without a repository allowlist. This does not prove the absence of every possible secret; review public screenshots, release contents, and unusual credentials too.

NuGet auditing checks direct and transitive dependencies at every severity. An explicit public audit source, warnings as errors, and a fresh HTTP cache during validation make retrieval failures fatal. The JSON vulnerability report is checked as well as the command exit status. Validation exercises isolated negative fixtures for wrong download hashes, a synthetic secret, stale lock files, unavailable audit data, and vulnerable transitive dependencies. It then runs the full acceptance suite, including temporary Windows scheduled tasks, and verifies both protected and password-free repositories created with restic 0.18.0 can be read and restored with 0.19.1.

## Intentional dependency updates

Normal builds and publishes use locked restores. Do not delete lock files to bypass a failure. After reviewing a dependency change, regenerate locks explicitly from the repository root:

```powershell
$sdk = & .\scripts\Bootstrap.ps1
& $sdk restore .\RepoBackup.slnx -p:RestoreLockedMode=false --force-evaluate
if ($LASTEXITCODE -ne 0) { throw 'Lock-file regeneration failed.' }
git diff -- '*packages.lock.json'
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Verify.ps1 -Package
```

Review and commit project declarations and all affected lock files together. The shared `win-x64` runtime identifier keeps the normal restore graph consistent with publishing. Do not suppress audit failures to make an update pass; investigate unavailable feeds or upgrade vulnerable dependencies.

Dependabot checks NuGet declarations in all five project directories and GitHub Actions weekly, with at most five open version-update PRs per ecosystem. Alerts and security-update PRs are enabled in repository settings; GitHub manages their separate limits. There is no auto-merge. Review every update and its passing checks before merging. Keep secret scanning and push protection enabled.

## Native tools and SDK

NuGet monitoring does not cover downloaded executables. Before each release, and at least monthly, review official upstream releases and security advisories for:

- [restic](https://github.com/restic/restic/releases), including backup-format compatibility and fixes affecting Windows restores.
- [Git for Windows / MinGit](https://github.com/git-for-windows/git/releases) and [Git LFS](https://github.com/git-lfs/git-lfs/releases).
- [Inno Setup](https://github.com/jrsoftware/issrc/releases).
- [Gitleaks](https://github.com/gitleaks/gitleaks/releases) and the [.NET SDK](https://dotnet.microsoft.com/download/dotnet).

Pin native tool versions, official download URLs, archive hashes, and applicable executable hashes in `scripts/packaging/Dependencies.psd1`. Compare downloads with upstream checksum publications/release digests before changing pins. For restic, update `ResticClient.Version` and `ResticClient.ExecutableSha256` together with the downloader metadata. SDK updates require both `global.json` and the version/URL/SHA-512 in `scripts/Bootstrap.ps1`. Rerun validation and clean-machine packaging checks after updates. The legacy restic 0.18.0 download is exclusively a compatibility-test fixture and is never shipped.

## GitHub automation and cost

`.github/workflows/verify.yml` runs on pull requests, pushes to `main`, and manual dispatch. It uses standard temporary GitHub-hosted Windows runners, a full-commit-pinned checkout with credential persistence disabled, `contents: read`, a 45-minute timeout, and cancellation of superseded runs. There are no repository secrets, write permissions, privileged follow-up workflows, self-hosted/larger runners, release uploads, or stored build artifacts. Avoid introducing `pull_request_target` or running unreviewed code in a privileged job.

[Dependabot alerts and update PRs are free](https://docs.github.com/en/code-security/getting-started/github-security-features). [Standard hosted runner use is free for public repositories](https://docs.github.com/en/billing/concepts/product-billing/github-actions); this configuration avoids larger runners and artifact storage. Keep the repository public for this cost assumption. Dependabot's own GitHub-managed update jobs are separate from the read-only validation job and need permission to create their update PRs.

Releases are built and uploaded manually using [the release procedure](PACKAGING.md). Published installers remain unsigned. Code signing is deferred; there is no signing service integration or certificate purchase in this setup.
