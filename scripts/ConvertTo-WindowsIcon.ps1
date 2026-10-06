<#
.SYNOPSIS
Creates a multi-resolution Windows icon from a square image, preserving transparency.
.EXAMPLE
.\scripts\ConvertTo-WindowsIcon.ps1 -InputFile .\src\RepoBackup.Desktop\Assets\RepoBackup.png -OutputFile .\src\RepoBackup.Desktop\Assets\RepoBackup.ico -Force
#>
param(
    [Parameter(Mandatory)][string]$InputFile,
    [Parameter(Mandatory)][string]$OutputFile,
    [ValidateRange(1,256)][int[]]$Sizes = @(16,20,24,32,40,48,64,128,256),
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskInput = (Resolve-Path -LiteralPath $InputFile).Path
$taskOutput = [IO.Path]::GetFullPath($OutputFile)
if ($taskInput -eq $taskOutput) { throw 'The input image and output icon must be different files.' }
if ((Test-Path -LiteralPath $taskOutput) -and -not $Force) { throw 'Output already exists. Use -Force to replace it.' }
$taskSizes = @($Sizes | Sort-Object -Unique)
if ($taskSizes.Count -eq 0) { throw 'At least one icon size is required.' }

$taskImage = [Drawing.Image]::FromFile($taskInput)
try {
    if ($taskImage.Width -ne $taskImage.Height) { throw 'The source image must be square.' }
    $taskFrames = [Collections.Generic.List[byte[]]]::new()
    foreach ($taskSize in $taskSizes) {
        $taskBitmap = [Drawing.Bitmap]::new($taskSize, $taskSize, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $taskGraphics = [Drawing.Graphics]::FromImage($taskBitmap)
            try {
                $taskGraphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
                $taskGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $taskGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $taskGraphics.Clear([Drawing.Color]::Transparent)
                $taskGraphics.DrawImage($taskImage, 0, 0, $taskSize, $taskSize)
            }
            finally { $taskGraphics.Dispose() }
            $taskStream = [IO.MemoryStream]::new()
            try {
                $taskBitmap.Save($taskStream, [Drawing.Imaging.ImageFormat]::Png)
                $taskFrames.Add($taskStream.ToArray())
            }
            finally { $taskStream.Dispose() }
        }
        finally { $taskBitmap.Dispose() }
    }

    # Modern Windows supports lossless PNG entries in ICO files. A zero dimension means 256 pixels.
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskOutput)) | Out-Null
    $taskWriter = [IO.BinaryWriter]::new([IO.File]::Create($taskOutput))
    try {
        $taskWriter.Write([uint16]0)
        $taskWriter.Write([uint16]1)
        $taskWriter.Write([uint16]$taskFrames.Count)
        $taskOffset = 6 + 16 * $taskFrames.Count
        for ($taskIndex = 0; $taskIndex -lt $taskFrames.Count; $taskIndex++) {
            $taskDimension = if ($taskSizes[$taskIndex] -eq 256) { 0 } else { $taskSizes[$taskIndex] }
            $taskWriter.Write([byte]$taskDimension)
            $taskWriter.Write([byte]$taskDimension)
            $taskWriter.Write([byte]0)
            $taskWriter.Write([byte]0)
            $taskWriter.Write([uint16]1)
            $taskWriter.Write([uint16]32)
            $taskWriter.Write([uint32]$taskFrames[$taskIndex].Length)
            $taskWriter.Write([uint32]$taskOffset)
            $taskOffset += $taskFrames[$taskIndex].Length
        }
        foreach ($taskFrame in $taskFrames) { $taskWriter.Write([byte[]]$taskFrame) }
    }
    finally { $taskWriter.Dispose() }
}
finally { $taskImage.Dispose() }
Write-Output $taskOutput
