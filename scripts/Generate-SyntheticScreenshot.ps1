param([string]$Output)
$ErrorActionPreference = 'Stop'

$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskTest = Join-Path $taskRoot 'tests\RepoBackup.Tests\bin\Release\net10.0-windows\RepoBackup.Tests.exe'
$taskDesktop = Join-Path $taskRoot 'src\RepoBackup.Desktop\bin\Release\net10.0-windows\RepoBackup.exe'
if (-not (Test-Path -LiteralPath $taskTest) -or -not (Test-Path -LiteralPath $taskDesktop)) {
    throw 'Build the Release solution before generating the screenshot.'
}

$taskOutput = if ($Output) { [IO.Path]::GetFullPath($Output) } else { Join-Path $taskRoot 'docs\images\project-dashboard.png' }
$taskFixture = Join-Path $taskRoot ('.artifacts\public-screenshot\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskFixture -Force | Out-Null
$taskDrive = @('P','Q','R','S','T') | Where-Object { -not (Test-Path -LiteralPath ('{0}:\' -f $_)) } | Select-Object -First 1
if (-not $taskDrive) { throw 'No free demo drive letter is available.' }
$taskDriveName = '{0}:' -f $taskDrive
$taskDriveRoot = '{0}\' -f $taskDriveName

$taskMounted = $false
try {
    & subst.exe $taskDriveName $taskFixture
    if ($LASTEXITCODE -ne 0) { throw 'Could not mount the isolated screenshot fixture.' }
    $taskMounted = $true
    if (-not (Test-Path -LiteralPath $taskDriveRoot)) { throw 'Could not access the isolated screenshot fixture.' }

    & $taskTest --public-screenshot-fixture $taskDriveRoot
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic fixture preparation failed.' }

    $taskPreview = Join-Path $taskFixture 'preview.png'
    $taskStart = [Diagnostics.ProcessStartInfo]::new($taskDesktop)
    $taskStart.UseShellExecute = $false
    $taskStart.CreateNoWindow = $true
    $taskStart.Arguments = '--data-dir "{0}" --render-preview "{1}" --preview-project "Sample Audio Suite" --synthetic-preview' -f (Join-Path $taskDriveRoot 'catalog'), $taskPreview
    $taskProcess = [Diagnostics.Process]::Start($taskStart)
    if (-not $taskProcess.WaitForExit(60000)) { $taskProcess.Kill(); throw 'Desktop screenshot rendering timed out.' }
    if ($taskProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $taskPreview)) { throw 'Desktop screenshot rendering failed.' }
    if ((Test-Path -LiteralPath ($taskPreview + '.bindings.txt')) -and (Get-Item -LiteralPath ($taskPreview + '.bindings.txt')).Length -gt 0) {
        throw 'Desktop screenshot rendering reported binding errors.'
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $taskOutput) -Force | Out-Null
    Copy-Item -LiteralPath $taskPreview -Destination $taskOutput -Force
    Write-Host "Synthetic dashboard screenshot: $taskOutput"
}
finally {
    if ($taskMounted) { & subst.exe $taskDriveName /D | Out-Null }
}
