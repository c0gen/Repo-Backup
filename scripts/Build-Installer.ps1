param([string]$Version, [string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'packaging\Package.psm1') -Force
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskPackage = if ($PackageDirectory) { [IO.Path]::GetFullPath($PackageDirectory) } else { Join-Path $taskRoot 'dist\RepoBackup' }
$taskManifest = Test-PackageManifest -Path $taskPackage
if (-not $Version) { $Version = $taskManifest.version }
$taskVersion = Get-PackageVersion -Version $Version
if ($taskManifest.version -ne $taskVersion.Version) { throw 'Installer version must match the published package. Publish again with -Version.' }
$taskCompiler = & (Join-Path $PSScriptRoot 'Bootstrap-InnoSetup.ps1')
$taskDist = Join-Path $taskRoot 'dist'
$taskSetupName = "RepoBackup-$Version-win-x64-setup.exe"
& $taskCompiler '/Qp' "/DPackageDir=$taskPackage" "/DPackageVersion=$Version" "/DPackageFileVersion=$($taskVersion.FileVersion)" "/O$taskDist" (Join-Path $taskRoot 'installer\RepoBackup.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
Write-ReleaseChecksums -Directory $taskDist -Files @("RepoBackup-$Version-win-x64.zip", $taskSetupName)
Write-Host "Installer: $(Join-Path $taskDist $taskSetupName)"
