$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\packaging\SmokeTest.psm1') -Force
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
# Test-only engine: never copied into an application package or used with user data.
$taskArchive = & (Join-Path $PSScriptRoot '..\packaging\Get-VerifiedDownload.ps1') -Url 'https://github.com/restic/restic/releases/download/v0.18.0/restic_0.18.0_windows_amd64.zip' -Sha256 'C90CFCD577FE3D60D2529021E76BD5637BDCD19D7FA84840A40FCBBF995902DE'
$taskLegacyDirectory = Join-Path $taskRoot '.tools\restic-0.18.0'
Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskLegacyDirectory -Force
$taskLegacy = Join-Path $taskLegacyDirectory 'restic_0.18.0_windows_amd64.exe'
if ((Get-FileHash -LiteralPath $taskLegacy -Algorithm SHA256).Hash -ne 'AC345161C31BFC5554A693ABB1999FADD81C9829CE881DC3868BF5B02405AB0D') { throw 'Legacy fixture engine checksum mismatch.' }
& (Join-Path $PSScriptRoot '..\Download-Restic.ps1')
$taskCurrent = Join-Path $taskRoot 'vendor\restic\restic.exe'
$taskFixture = Join-Path $taskRoot ('.artifacts\restic-compatibility\' + [Guid]::NewGuid().ToString('N'))
$taskSaved = @{}
foreach ($taskName in @('RESTIC_PASSWORD', 'RESTIC_PASSWORD_FILE', 'RESTIC_PASSWORD_COMMAND', 'RESTIC_KEY_HINT', 'RESTIC_REPOSITORY_FILE', 'RESTIC_CACHE_DIR')) {
    $taskSaved[$taskName] = [Environment]::GetEnvironmentVariable($taskName)
    [Environment]::SetEnvironmentVariable($taskName, $null)
}
try {
    $env:RESTIC_CACHE_DIR = Join-Path $taskFixture 'cache'
    foreach ($taskMode in @('protected', 'password-free')) {
        $taskModeRoot = Join-Path $taskFixture $taskMode
        New-Item -ItemType Directory -Path $taskModeRoot -Force | Out-Null
        $taskSource = Join-Path $taskModeRoot 'fixture.txt'
        [IO.File]::WriteAllText($taskSource, 'Restic 0.18.0 compatibility fixture: ' + [Guid]::NewGuid())
        $taskBefore = (Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash
        $taskRepository = Join-Path $taskModeRoot 'repository'
        $taskRestore = Join-Path $taskModeRoot 'restored'
        $taskArguments = @('--repo', $taskRepository, '--json')
        if ($taskMode -eq 'password-free') { $env:RESTIC_PASSWORD = ''; $taskArguments += '--insecure-no-password' }
        else { $env:RESTIC_PASSWORD = [Guid]::NewGuid().ToString('N') }
        $null = Invoke-SmokeProcess -Executable $taskLegacy -Arguments ($taskArguments + @('init'))
        $null = Invoke-SmokeProcess -Executable $taskLegacy -Arguments ($taskArguments + @('backup', $taskSource))
        $taskSnapshots = Invoke-SmokeProcess -Executable $taskCurrent -Arguments ($taskArguments + @('snapshots')) | ConvertFrom-Json
        if (@($taskSnapshots).Count -ne 1) { throw 'Could not read the legacy snapshot.' }
        $null = Invoke-SmokeProcess -Executable $taskCurrent -Arguments ($taskArguments + @('check', '--read-data'))
        $null = Invoke-SmokeProcess -Executable $taskCurrent -Arguments ($taskArguments + @('restore', 'latest', '--target', $taskRestore))
        $taskRestored = @(Get-ChildItem -LiteralPath $taskRestore -Recurse -File -Filter 'fixture.txt')
        if ($taskRestored.Count -ne 1 -or (Get-FileHash -LiteralPath $taskRestored[0].FullName -Algorithm SHA256).Hash -ne $taskBefore) { throw "Legacy $taskMode restore hash mismatch." }
        Write-Host "PASS restic 0.18.0 $taskMode repository reads, verifies, and restores with restic 0.19.1."
    }
}
finally {
    foreach ($taskName in $taskSaved.Keys) { [Environment]::SetEnvironmentVariable($taskName, $taskSaved[$taskName]) }
}
