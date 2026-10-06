Set-StrictMode -Version Latest

function Assert-NuGetAuditReport {
    param([Parameter(Mandatory)][string]$Json, [Parameter(Mandatory)][int]$ProjectCount)
    $taskReport = $Json | ConvertFrom-Json
    if ($taskReport.version -ne 1 -or @($taskReport.projects).Count -ne $ProjectCount) { throw 'Incomplete NuGet audit report.' }
    # A successful process exit alone is insufficient: package list can return 0
    # with vulnerabilities or diagnostics in the JSON report.
    if ($taskReport.PSObject.Properties['logs'] -and @($taskReport.logs).Count) { throw 'NuGet audit reported diagnostics; vulnerability data may be unavailable.' }
    foreach ($taskProject in $taskReport.projects) {
        if ($taskProject.PSObject.Properties['logs'] -and @($taskProject.logs).Count) { throw "NuGet audit failed for $($taskProject.path)." }
        if (-not $taskProject.PSObject.Properties['frameworks']) { continue }
        foreach ($taskFramework in $taskProject.frameworks) {
            foreach ($taskKind in @('topLevelPackages', 'transitivePackages')) {
                if ($taskFramework.PSObject.Properties[$taskKind] -and @($taskFramework.$taskKind).Count) {
                    throw "Known vulnerable dependencies in $($taskProject.path). See nuget-audit.json."
                }
            }
        }
    }
}

function Invoke-NuGetAudit {
    param([Parameter(Mandatory)][string]$Sdk, [Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Reports)
    New-Item -ItemType Directory -Path $Reports -Force | Out-Null
    $taskPreviousCache = $env:NUGET_HTTP_CACHE_PATH
    try {
        # A fresh cache and --no-http-cache prevent a cached vulnerability feed
        # from disguising an outage. NU1900-NU1905 are errors via repository props.
        $env:NUGET_HTTP_CACHE_PATH = Join-Path $Reports 'nuget-http-cache'
        & $Sdk restore (Join-Path $Root 'RepoBackup.slnx') --locked-mode --force --no-http-cache --disable-parallel --disable-build-servers -m:1 -v minimal -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=low -p:TreatWarningsAsErrors=true -p:RestoreIgnoreFailedSources=false
        if ($LASTEXITCODE -ne 0) { throw 'Locked dependency restore / live vulnerability audit failed.' }
        $taskJson = (& $Sdk package list --project (Join-Path $Root 'RepoBackup.slnx') --vulnerable --include-transitive --no-restore --format json --output-version 1) -join "`n"
        if ($LASTEXITCODE -ne 0) { throw 'NuGet vulnerability report failed.' }
        $taskJson | Set-Content -LiteralPath (Join-Path $Reports 'nuget-audit.json') -Encoding UTF8
        $taskSolution = [xml](Get-Content -LiteralPath (Join-Path $Root 'RepoBackup.slnx') -Raw)
        Assert-NuGetAuditReport -Json $taskJson -ProjectCount @($taskSolution.SelectNodes('//Project')).Count
    }
    finally { $env:NUGET_HTTP_CACHE_PATH = $taskPreviousCache }
    Write-Host 'PASS live NuGet audit, including transitive dependencies.'
}

Export-ModuleMember -Function Invoke-NuGetAudit, Assert-NuGetAuditReport
