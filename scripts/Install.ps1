param()
$ErrorActionPreference = 'Stop'
$taskSource = [IO.Path]::GetFullPath($PSScriptRoot)
if (-not (Test-Path -LiteralPath (Join-Path $taskSource 'RepoBackup.exe'))) { throw 'Run Install.ps1 from the published dist\RepoBackup package.' }
$taskPrograms = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'))
$taskInstall = [IO.Path]::GetFullPath((Join-Path $taskPrograms 'RepoBackup'))
if (-not $taskInstall.StartsWith($taskPrograms + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid per-user installation target.' }
foreach ($taskProcess in (Get-Process -Name 'RepoBackup','RepoBackup.Cli','RepoBackup.Runner' -ErrorAction SilentlyContinue)) {
    if ($taskProcess.Path -and $taskProcess.Path.StartsWith($taskInstall + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Close Repo Backup and wait for scheduled operations before updating.' }
}
$taskManifest = Get-Content -LiteralPath (Join-Path $taskSource 'package-manifest.json') -Raw | ConvertFrom-Json
foreach ($taskFile in $taskManifest.files) {
    $taskPath = [IO.Path]::GetFullPath((Join-Path $taskSource $taskFile.path))
    if (-not $taskPath.StartsWith($taskSource + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid package path.' }
    if ((Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash -ne $taskFile.sha256) { throw "Package verification failed: $($taskFile.path)" }
}
New-Item -ItemType Directory -Path $taskInstall -Force | Out-Null
Get-ChildItem -LiteralPath $taskSource | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskInstall -Recurse -Force }
$taskMenu = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'
New-Item -ItemType Directory -Path $taskMenu -Force | Out-Null
$taskShell = New-Object -ComObject WScript.Shell
$taskShortcut = $taskShell.CreateShortcut((Join-Path $taskMenu 'Repo Backup.lnk'))
$taskShortcut.TargetPath = Join-Path $taskInstall 'RepoBackup.exe'; $taskShortcut.WorkingDirectory = $taskInstall; $taskShortcut.Save()
Write-Host "Installed for this user at $taskInstall. Scheduling remains off until configured in Settings."
