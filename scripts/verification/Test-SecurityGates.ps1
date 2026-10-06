param([Parameter(Mandatory)][string]$Sdk, [Parameter(Mandatory)][string]$Scanner, [Parameter(Mandatory)][string]$Reports)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'NuGetAudit.psm1') -Force
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$taskFixture = Join-Path $Reports 'gate-fixtures'
New-Item -ItemType Directory -Path $taskFixture -Force | Out-Null

function Assert-Rejected {
    param([scriptblock]$Action, [string]$Expected)
    try { & $Action } catch {
        if ($_.Exception.Message -notmatch $Expected) { throw }
        return
    }
    throw "Security gate unexpectedly succeeded: $Expected"
}

$taskDependency = (Import-PowerShellDataFile (Join-Path $PSScriptRoot '..\packaging\Dependencies.psd1')).Gitleaks
Assert-Rejected -Expected 'checksum mismatch' -Action {
    & (Join-Path $PSScriptRoot '..\packaging\Get-VerifiedDownload.ps1') -Url $taskDependency.Url -Sha256 ('0' * 64)
}
Write-Host 'PASS incorrect download checksum is rejected.'

# Construct a nonfunctional canary only inside an ignored, isolated Git fixture.
$taskSecretRoot = Join-Path $taskFixture 'secret-repo'
$taskSecretReports = Join-Path $taskFixture 'secret-reports'
& git init --quiet $taskSecretRoot
if ($LASTEXITCODE -ne 0) { throw 'Secret fixture Git initialization failed.' }
& git -C $taskSecretRoot -c user.name=Fixture -c user.email=fixture@example.invalid -c commit.gpgsign=false -c "core.hooksPath=$taskFixture\no-hooks" commit --allow-empty --quiet -m 'Empty scan fixture'
if ($LASTEXITCODE -ne 0) { throw 'Secret fixture commit failed.' }
$taskCanary = 'ghp_' + [Guid]::NewGuid().ToString('N') + 'aB9Z'
[IO.File]::WriteAllText((Join-Path $taskSecretRoot 'fixture.txt'), "GITHUB_TOKEN=$taskCanary")
Assert-Rejected -Expected 'Gitleaks source scan failed' -Action {
    & (Join-Path $PSScriptRoot 'Test-Secrets.ps1') -Scanner $Scanner -Root $taskSecretRoot -Reports $taskSecretReports
}
$taskRedacted = Get-Content -LiteralPath (Join-Path $taskSecretReports 'gitleaks-source.json') -Raw
if ($taskRedacted.Contains($taskCanary) -or @($taskRedacted | ConvertFrom-Json).Count -eq 0) { throw 'Synthetic secret was not detected and redacted.' }
Write-Host 'PASS isolated synthetic secret blocks validation and is redacted.'

# Change a declaration in a copy, leaving every real project/lock file untouched.
$taskProjectRoot = Join-Path $taskFixture 'stale-lock'
New-Item -ItemType Directory -Path $taskProjectRoot -Force | Out-Null
$taskProject = Join-Path $taskProjectRoot 'Fixture.csproj'
$taskOriginal = Get-Content -LiteralPath (Join-Path $taskRoot 'src\RepoBackup.Core\RepoBackup.Core.csproj') -Raw
$taskStale = $taskOriginal -replace '(PackageReference Include="Microsoft.Data.Sqlite" Version=")[^"]+', '${1}0.0.1'
if ($taskStale -eq $taskOriginal) { throw 'Stale-lock fixture needs updating for the current dependency declarations.' }
[IO.File]::WriteAllText($taskProject, $taskStale)
Copy-Item -LiteralPath (Join-Path $taskRoot 'src\RepoBackup.Core\packages.lock.json') -Destination $taskProjectRoot
$taskOutput = (& $Sdk restore $taskProject --locked-mode --disable-build-servers -m:1 -v minimal 2>&1) -join "`n"
if ($LASTEXITCODE -eq 0 -or $taskOutput -notmatch 'NU1004') { throw "Stale lock was not rejected with NU1004: $taskOutput" }
Write-Host 'PASS stale dependency lock is rejected.'

# Prove an unreachable audit source fails even with all package files cached.
[IO.File]::WriteAllText($taskProject, $taskOriginal)
$taskConfig = Join-Path $taskProjectRoot 'NuGet.Config'
@'
<configuration>
  <packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <auditSources><clear /><add key="unreachable-fixture" value="https://127.0.0.1:1/index.json" /></auditSources>
</configuration>
'@ | Set-Content -LiteralPath $taskConfig -Encoding UTF8
$taskOutput = (& $Sdk restore $taskProject --locked-mode --force --no-http-cache --configfile $taskConfig --disable-build-servers -m:1 -v minimal 2>&1) -join "`n"
if ($LASTEXITCODE -eq 0 -or $taskOutput -notmatch 'NU1900') { throw "Unavailable audit data was not rejected with NU1900: $taskOutput" }
Write-Host 'PASS unavailable vulnerability feed blocks validation.'

Assert-Rejected -Expected 'Known vulnerable' -Action {
    Assert-NuGetAuditReport -ProjectCount 1 -Json '{"version":1,"projects":[{"path":"fixture","frameworks":[{"transitivePackages":[{"id":"Synthetic.Vulnerability","vulnerabilities":[{"severity":"Low"}]}]}]}]}'
}
Assert-Rejected -Expected 'diagnostics' -Action {
    Assert-NuGetAuditReport -ProjectCount 1 -Json '{"version":1,"projects":[{"path":"fixture"}],"logs":[{"level":"error","message":"Unavailable audit data"}]}'
}
Write-Host 'PASS vulnerable transitive packages and audit errors block validation.'
