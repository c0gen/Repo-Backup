param([switch]$Package, [switch]$Installer, [string]$Version)
$ErrorActionPreference = 'Stop'
# Windows PowerShell's per-buffer download progress is expensive on clean CI.
$ProgressPreference = 'SilentlyContinue'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskReports = Join-Path $taskRoot ('.artifacts\verification\' + [Guid]::NewGuid().ToString('N'))
Import-Module (Join-Path $PSScriptRoot 'verification\NuGetAudit.psm1') -Force
Push-Location $taskRoot
try {
    $taskScanner = & (Join-Path $PSScriptRoot 'verification\Get-Gitleaks.ps1')
    & (Join-Path $PSScriptRoot 'verification\Test-Secrets.ps1') -Scanner $taskScanner -Root $taskRoot -Reports $taskReports
    $taskSdk = & (Join-Path $PSScriptRoot 'Bootstrap.ps1')
    Invoke-NuGetAudit -Sdk $taskSdk -Root $taskRoot -Reports $taskReports
    & (Join-Path $PSScriptRoot 'verification\Test-SecurityGates.ps1') -Sdk $taskSdk -Scanner $taskScanner -Reports $taskReports
    & (Join-Path $PSScriptRoot 'Build.ps1') -Test -WindowsIntegration -Publish:$Package -Installer:$Installer -Version $Version
    & (Join-Path $PSScriptRoot 'verification\Test-ResticCompatibility.ps1')
    if ($Package -or $Installer) {
        & (Join-Path $PSScriptRoot 'Test-Package.ps1') -Version $Version -Installer:$Installer
    }
    Write-Host "PASS validation. Local reports: $taskReports"
}
finally { Pop-Location }
