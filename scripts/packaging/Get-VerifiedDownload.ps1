param([Parameter(Mandatory)][string]$Url, [Parameter(Mandatory)][string]$Sha256)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$taskDownloads = Join-Path $taskRoot '.tools\downloads'
New-Item -ItemType Directory -Path $taskDownloads -Force | Out-Null
$taskFile = Join-Path $taskDownloads ([IO.Path]::GetFileName(([Uri]$Url).AbsolutePath))
if (-not (Test-Path -LiteralPath $taskFile)) {
    # Do not leave a partial download in the cache after a network failure.
    $taskPartial = "$taskFile.partial"
    Invoke-WebRequest -Uri $Url -OutFile $taskPartial -UseBasicParsing
    if ((Get-FileHash -LiteralPath $taskPartial -Algorithm SHA256).Hash -ne $Sha256) { throw "Download checksum mismatch: $Url" }
    Move-Item -LiteralPath $taskPartial -Destination $taskFile -Force
}
if ((Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash -ne $Sha256) { throw "Cached download checksum mismatch: $taskFile" }
return $taskFile
