param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskVersion = '0.18.0'
$taskArchiveHash = 'C90CFCD577FE3D60D2529021E76BD5637BDCD19D7FA84840A40FCBBF995902DE'
$taskExeHash = 'AC345161C31BFC5554A693ABB1999FADD81C9829CE881DC3868BF5B02405AB0D'
$taskVendor = Join-Path $taskRoot 'vendor\restic'
$taskExe = Join-Path $taskVendor 'restic.exe'
New-Item -ItemType Directory -Path $taskVendor -Force | Out-Null
if ((Test-Path -LiteralPath $taskExe) -and (Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash -eq $taskExeHash) { return }
$taskDownloads = Join-Path $taskRoot '.tools\downloads'
New-Item -ItemType Directory -Path $taskDownloads -Force | Out-Null
$taskArchive = Join-Path $taskDownloads "restic_$($taskVersion)_windows_amd64.zip"
if (-not (Test-Path -LiteralPath $taskArchive)) { Invoke-WebRequest "https://github.com/restic/restic/releases/download/v$taskVersion/restic_$($taskVersion)_windows_amd64.zip" -OutFile $taskArchive }
if ((Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash -ne $taskArchiveHash) { throw 'Restic archive checksum mismatch. The executable was not installed.' }
$taskExtract = Join-Path $taskDownloads 'restic-extracted'
Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskExtract -Force
$taskExtractedExe = Join-Path $taskExtract "restic_$($taskVersion)_windows_amd64.exe"
if ((Get-FileHash -LiteralPath $taskExtractedExe -Algorithm SHA256).Hash -ne $taskExeHash) { throw 'Restic executable checksum mismatch.' }
Copy-Item -LiteralPath $taskExtractedExe -Destination $taskExe -Force
Write-Host "Verified restic $taskVersion."
