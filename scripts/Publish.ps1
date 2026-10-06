param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [string]$Sdk)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Sdk) { $Sdk = & (Join-Path $PSScriptRoot 'Bootstrap.ps1') }
$taskOutput = Join-Path $taskRoot 'dist\RepoBackup'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
foreach ($taskProject in @('RepoBackup.Desktop','RepoBackup.Cli','RepoBackup.Runner')) {
    & $Sdk publish (Join-Path $taskRoot "src\$taskProject\$taskProject.csproj") -c $Configuration -r win-x64 --self-contained true -o $taskOutput --disable-build-servers -m:1 -v minimal -p:PublishSingleFile=false -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) { throw "Publishing $taskProject failed." }
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'scripts\Install.ps1') -Destination (Join-Path $taskOutput 'Install.ps1') -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\RECOVERY.md') -Destination (Join-Path $taskOutput 'RECOVERY.md') -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination $taskOutput -Recurse -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination (Join-Path $taskOutput 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE') -Destination (Join-Path $taskOutput 'LICENSE') -Force
$taskManifest = Get-ChildItem -LiteralPath $taskOutput -File -Recurse | Where-Object { $_.Name -ne 'package-manifest.json' } | ForEach-Object {
    [pscustomobject]@{ path=$_.FullName.Substring($taskOutput.Length+1); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
}
@{version='1.0.0';architecture='win-x64';files=@($taskManifest)} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskOutput 'package-manifest.json') -Encoding UTF8
Compress-Archive -Path (Join-Path $taskOutput '*') -DestinationPath (Join-Path $taskRoot 'dist\RepoBackup-win-x64.zip') -Force
Write-Host "Self-contained package: $taskOutput"
