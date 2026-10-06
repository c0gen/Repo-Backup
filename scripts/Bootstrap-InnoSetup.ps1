param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskDependency = (Import-PowerShellDataFile (Join-Path $PSScriptRoot 'packaging\Dependencies.psd1')).InnoSetup
$taskDirectory = Join-Path $taskRoot ".tools\inno-setup-$($taskDependency.Version)"
$taskCompiler = Join-Path $taskDirectory 'ISCC.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) {
    $taskSetup = & (Join-Path $PSScriptRoot 'packaging\Get-VerifiedDownload.ps1') -Url $taskDependency.Url -Sha256 $taskDependency.Sha256
    # Official portable mode installs only build tools here, without an uninstaller,
    # file associations, Start menu entries, or machine-wide prerequisites.
    $taskProcess = Start-Process -FilePath $taskSetup -ArgumentList @('/PORTABLE=1', '/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOICONS', '/TASKS=""', "/DIR=`"$taskDirectory`"") -WindowStyle Hidden -Wait -PassThru
    if ($taskProcess.ExitCode -ne 0) { throw "Inno Setup bootstrap failed (exit $($taskProcess.ExitCode))." }
}
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw 'Inno Setup compiler is missing.' }
return $taskCompiler
