param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSdk = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
$taskSdkVersion = '10.0.401'
if (-not (Test-Path -LiteralPath $taskSdk)) {
    $taskTools = Join-Path $taskRoot '.tools'
    New-Item -ItemType Directory -Path $taskTools -Force | Out-Null
    $taskZip = Join-Path $taskTools 'dotnet-sdk.zip'
    if (-not (Test-Path -LiteralPath $taskZip)) { Invoke-WebRequest 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-win-x64.zip' -OutFile $taskZip }
    $taskSha512 = '24B670AD3D923BFCF47DF6C3B034152398B42F6DBC388E10D783AEE1CFB5E5817D399FC0AE2A12CFA822A55E61D34830CCB15C50EF6EFEE437AB874BB7C79430'
    if ((Get-FileHash -LiteralPath $taskZip -Algorithm SHA512).Hash -ne $taskSha512) { throw 'SDK checksum mismatch.' }
    Expand-Archive -LiteralPath $taskZip -DestinationPath (Join-Path $taskTools 'dotnet') -Force
}
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
& (Join-Path $PSScriptRoot 'Download-Restic.ps1')
return $taskSdk
