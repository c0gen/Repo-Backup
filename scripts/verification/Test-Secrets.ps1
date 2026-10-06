param(
    [Parameter(Mandatory)][string]$Scanner,
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$Reports
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $Reports -Force | Out-Null
$taskOptions = @('--redact=100', '--no-banner', '--no-color', '--ignore-gitleaks-allow',
    '--config', (Join-Path $PSScriptRoot 'gitleaks.toml'), '--gitleaks-ignore-path', $Reports,
    '--report-format', 'json', '--max-decode-depth', '5')

# Complete history is required in CI too (checkout fetch-depth: 0).
$taskShallow = & git -C $Root rev-parse --is-shallow-repository
if ($LASTEXITCODE -ne 0 -or $taskShallow -ne 'false') { throw 'Secret scanning requires a non-shallow Git checkout.' }
& $Scanner git $Root '--log-opts=--all --full-history' @taskOptions --report-path (Join-Path $Reports 'gitleaks-history.json')
if ($LASTEXITCODE -ne 0) { throw 'Gitleaks history scan failed. Review the redacted report.' }

# Export exactly tracked files plus untracked, non-ignored files. Never scan local
# backup fixtures, credentials, downloaded tools, or build output just because they exist.
$taskFiles = (& git -C $Root ls-files -z --cached --others --exclude-standard) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate source files for secret scanning.' }
$taskReportRoot = [IO.Path]::GetFullPath($Reports).TrimEnd('\')
$taskSource = Join-Path $taskReportRoot ('source-' + [Guid]::NewGuid().ToString('N'))
try {
    foreach ($taskRelative in ($taskFiles -split "`0" | Where-Object { $_ })) {
        $taskInput = Join-Path $Root $taskRelative
        if (-not (Test-Path -LiteralPath $taskInput -PathType Leaf)) { continue } # Locally deleted file.
        if ((Get-Item -LiteralPath $taskInput -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Review linked source before scanning: $taskRelative" }
        $taskOutput = Join-Path $taskSource $taskRelative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($taskOutput)) -Force | Out-Null
        Copy-Item -LiteralPath $taskInput -Destination $taskOutput
    }
    if (-not (Test-Path -LiteralPath $taskSource)) { throw 'No source files were available to scan.' }
    & $Scanner dir $taskSource @taskOptions --report-path (Join-Path $Reports 'gitleaks-source.json')
    if ($LASTEXITCODE -ne 0) { throw 'Gitleaks source scan failed. Review the redacted report.' }
}
finally {
    # Keep only redacted reports, not an extra copy of potentially sensitive source.
    if (-not [IO.Path]::GetFullPath($taskSource).StartsWith($taskReportRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Source-scan cleanup escaped the report directory.' }
    if (Test-Path -LiteralPath $taskSource) { Remove-Item -LiteralPath $taskSource -Recurse -Force }
}
Write-Host 'PASS redacted secret scans of Git history and current source.'
