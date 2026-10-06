param()
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'packaging\Package.psm1') -Force
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskDependency = (Import-PowerShellDataFile (Join-Path $PSScriptRoot 'packaging\Dependencies.psd1')).MinGit
$taskArchive = & (Join-Path $PSScriptRoot 'packaging\Get-VerifiedDownload.ps1') -Url $taskDependency.Url -Sha256 $taskDependency.Sha256
$taskOutput = Join-Path $taskRoot ".tools\mingit-$($taskDependency.Version)"
Reset-PackageDirectory -Path $taskOutput -Root (Join-Path $taskRoot '.tools')
Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskOutput
if (-not (Test-Path -LiteralPath (Join-Path $taskOutput 'cmd\git.exe'))) { throw 'MinGit archive has no cmd\git.exe.' }
$taskLfs = & (Join-Path $PSScriptRoot 'Download-GitLfs.ps1')
Copy-Item -LiteralPath (Join-Path $taskLfs 'git-lfs.exe') -Destination (Join-Path $taskOutput 'cmd\git-lfs.exe')
New-Item -ItemType Directory -Path (Join-Path $taskOutput 'lfs') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot 'vendor\git-lfs\LICENSE') -Destination (Join-Path $taskOutput 'lfs\LICENSE')
Copy-Item -LiteralPath (Join-Path $taskLfs 'README.md') -Destination (Join-Path $taskOutput 'lfs\README.md')
Write-Host "Verified MinGit $($taskDependency.Version)."
return $taskOutput
