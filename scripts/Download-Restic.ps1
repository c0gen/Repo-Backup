param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskDependency = (Import-PowerShellDataFile (Join-Path $PSScriptRoot 'packaging\Dependencies.psd1')).Restic
$taskVersion = $taskDependency.Version
$taskExeHash = $taskDependency.ExecutableSha256
$taskVendor = Join-Path $taskRoot 'vendor\restic'
$taskExe = Join-Path $taskVendor 'restic.exe'
New-Item -ItemType Directory -Path $taskVendor -Force | Out-Null
if ((Test-Path -LiteralPath $taskExe) -and (Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash -eq $taskExeHash) { return }
$taskArchive = & (Join-Path $PSScriptRoot 'packaging\Get-VerifiedDownload.ps1') -Url $taskDependency.Url -Sha256 $taskDependency.Sha256
$taskExtract = Join-Path $taskRoot ".tools\restic-$taskVersion"
Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskExtract -Force
$taskExtractedExe = Join-Path $taskExtract "restic_$($taskVersion)_windows_amd64.exe"
if ((Get-FileHash -LiteralPath $taskExtractedExe -Algorithm SHA256).Hash -ne $taskExeHash) { throw 'Restic executable checksum mismatch.' }
Copy-Item -LiteralPath $taskExtractedExe -Destination $taskExe -Force
Write-Host "Verified restic $taskVersion."
