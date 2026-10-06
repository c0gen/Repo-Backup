param()
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'packaging\Package.psm1') -Force
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskDependency = (Import-PowerShellDataFile (Join-Path $PSScriptRoot 'packaging\Dependencies.psd1')).GitLfs
$taskArchive = & (Join-Path $PSScriptRoot 'packaging\Get-VerifiedDownload.ps1') -Url $taskDependency.Url -Sha256 $taskDependency.Sha256
$taskOutput = Join-Path $taskRoot ".tools\git-lfs-$($taskDependency.Version)"
Reset-PackageDirectory -Path $taskOutput -Root (Join-Path $taskRoot '.tools')
Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskOutput
$taskTools = Join-Path $taskOutput "git-lfs-$($taskDependency.Version)"
if (-not (Test-Path -LiteralPath (Join-Path $taskTools 'git-lfs.exe'))) { throw 'Git LFS archive has no git-lfs.exe.' }
Write-Host "Verified Git LFS $($taskDependency.Version)."
return $taskTools
