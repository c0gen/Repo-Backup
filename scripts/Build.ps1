param(
    [switch]$Test,
    [switch]$WindowsIntegration,
    [switch]$Publish,
    [switch]$Installer,
    [string]$Version,
    [string]$PackageDirectory,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'packaging\Package.psm1') -Force
$taskVersion = Get-PackageVersion -Version $Version
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSdk = & (Join-Path $PSScriptRoot 'Bootstrap.ps1')
Push-Location $taskRoot
try {
    & $taskSdk restore 'RepoBackup.slnx' --disable-parallel --disable-build-servers -m:1 -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    & $taskSdk build 'RepoBackup.slnx' -c $Configuration --no-restore --disable-build-servers -m:1 -v minimal "-p:Version=$($taskVersion.Version)"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if ($Test -or $WindowsIntegration) {
        if ($WindowsIntegration -and $Configuration -ne 'Release') { throw 'Windows integration tests require a Release build.' }
        $taskTestArguments = @()
        if ($WindowsIntegration) { $taskTestArguments += '--windows-integration' }
        & (Join-Path $taskRoot "tests\RepoBackup.Tests\bin\$Configuration\net10.0-windows\RepoBackup.Tests.exe") @taskTestArguments
        if ($LASTEXITCODE -ne 0) { throw 'Acceptance tests failed.' }
    }
    if ($Publish -or $Installer) { & (Join-Path $PSScriptRoot 'Publish.ps1') -Configuration $Configuration -Sdk $taskSdk -Version $taskVersion.Version -PackageDirectory $PackageDirectory }
    if ($Installer) { & (Join-Path $PSScriptRoot 'Build-Installer.ps1') -Version $taskVersion.Version -PackageDirectory $PackageDirectory }
}
finally { Pop-Location }
