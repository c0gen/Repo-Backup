$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$taskDependency = (Import-PowerShellDataFile (Join-Path $PSScriptRoot '..\packaging\Dependencies.psd1')).Gitleaks
$taskDirectory = Join-Path $taskRoot ".tools\gitleaks-$($taskDependency.Version)"
$taskExecutable = Join-Path $taskDirectory 'gitleaks.exe'
if (-not (Test-Path -LiteralPath $taskExecutable)) {
    $taskArchive = & (Join-Path $PSScriptRoot '..\packaging\Get-VerifiedDownload.ps1') -Url $taskDependency.Url -Sha256 $taskDependency.Sha256
    Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskDirectory -Force
}
if ((Get-FileHash -LiteralPath $taskExecutable -Algorithm SHA256).Hash -ne $taskDependency.ExecutableSha256) {
    throw 'Gitleaks executable checksum mismatch.'
}
return $taskExecutable
