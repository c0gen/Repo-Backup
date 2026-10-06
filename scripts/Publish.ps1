param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [string]$Sdk, [string]$Version, [string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'packaging\Package.psm1') -Force
$taskVersion = Get-PackageVersion -Version $Version
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Sdk) { $Sdk = & (Join-Path $PSScriptRoot 'Bootstrap.ps1') }
$taskGit = & (Join-Path $PSScriptRoot 'Download-MinGit.ps1')
$taskOutput = if ($PackageDirectory) { [IO.Path]::GetFullPath($PackageDirectory) } else { Join-Path $taskRoot 'dist\RepoBackup' }
$taskOutput = $taskOutput.TrimEnd('\')
Reset-PackageDirectory -Path $taskOutput -Root (Join-Path $taskRoot 'dist')
foreach ($taskProject in @('RepoBackup.Desktop','RepoBackup.Cli','RepoBackup.Runner')) {
    & $Sdk publish (Join-Path $taskRoot "src\$taskProject\$taskProject.csproj") -c $Configuration -r win-x64 --self-contained true -o $taskOutput --disable-build-servers -m:1 -v minimal -p:RestoreLockedMode=true -p:PublishSingleFile=false -p:PublishTrimmed=false "-p:Version=$($taskVersion.Version)"
    if ($LASTEXITCODE -ne 0) { throw "Publishing $taskProject failed." }
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'scripts\Install.ps1') -Destination (Join-Path $taskOutput 'Install.ps1') -Force
New-Item -ItemType Directory -Path (Join-Path $taskOutput 'packaging') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'packaging\Package.psm1') -Destination (Join-Path $taskOutput 'packaging\Package.psm1') -Force
Copy-Item -LiteralPath $taskGit -Destination (Join-Path $taskOutput 'git') -Recurse
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\RECOVERY.md') -Destination (Join-Path $taskOutput 'RECOVERY.md') -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination $taskOutput -Recurse -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination (Join-Path $taskOutput 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE') -Destination (Join-Path $taskOutput 'LICENSE') -Force
$taskManifest = Get-ChildItem -LiteralPath $taskOutput -File -Recurse | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{ path=$_.FullName.Substring($taskOutput.Length+1); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
}
@{version=$taskVersion.Version;architecture='win-x64';files=@($taskManifest)} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskOutput 'package-manifest.json') -Encoding UTF8
$null = Test-PackageManifest -Path $taskOutput
$taskZipName = "RepoBackup-$($taskVersion.Version)-win-x64.zip"
Compress-Archive -Path (Join-Path $taskOutput '*') -DestinationPath (Join-Path $taskRoot "dist\$taskZipName") -Force
Write-ReleaseChecksums -Directory (Join-Path $taskRoot 'dist') -Files @($taskZipName)
Write-Host "Self-contained package: $taskOutput"
