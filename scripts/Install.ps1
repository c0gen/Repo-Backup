param()
$ErrorActionPreference = 'Stop'
$taskSource = [IO.Path]::GetFullPath($PSScriptRoot)
if (-not (Test-Path -LiteralPath (Join-Path $taskSource 'RepoBackup.exe'))) { throw 'Run Install.ps1 from the published dist\RepoBackup package.' }
Import-Module (Join-Path $taskSource 'packaging\Package.psm1') -Force
$taskManifest = Test-PackageManifest -Path $taskSource
$taskPrograms = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'))
$taskInstall = [IO.Path]::GetFullPath((Join-Path $taskPrograms 'RepoBackup'))
if (-not $taskInstall.StartsWith($taskPrograms + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid per-user installation target.' }
foreach ($taskProcess in (Get-Process -Name 'RepoBackup','RepoBackup.Cli','RepoBackup.Runner' -ErrorAction SilentlyContinue)) {
    if ($taskProcess.Path -and $taskProcess.Path.StartsWith($taskInstall + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Close Repo Backup and wait for scheduled operations before updating.' }
}
New-Item -ItemType Directory -Path $taskInstall -Force | Out-Null
foreach ($taskFile in $taskManifest.files) {
    $taskDestination = Join-Path $taskInstall $taskFile.path
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($taskDestination)) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskSource $taskFile.path) -Destination $taskDestination -Force
}
Copy-Item -LiteralPath (Join-Path $taskSource 'package-manifest.json') -Destination $taskInstall -Force
$taskMenu = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'
New-Item -ItemType Directory -Path $taskMenu -Force | Out-Null
$taskShell = New-Object -ComObject WScript.Shell
$taskShortcut = $taskShell.CreateShortcut((Join-Path $taskMenu 'Repo Backup.lnk'))
$taskShortcut.TargetPath = Join-Path $taskInstall 'RepoBackup.exe'; $taskShortcut.WorkingDirectory = $taskInstall; $taskShortcut.Save()
Write-Host "Installed for this user at $taskInstall. Scheduling remains off until configured in Settings."
