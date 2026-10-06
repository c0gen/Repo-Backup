Set-StrictMode -Version Latest

function Get-PackageVersion {
    param([string]$Version)
    if (-not $Version) {
        $taskProps = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\Directory.Build.props') -Raw)
        $Version = [string]$taskProps.Project.PropertyGroup.Version
    }
    if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z') {
        throw 'Use a version such as 1.2.3 or 1.2.3-rc.1.'
    }
    $taskComponents = @($Matches[1], $Matches[2], $Matches[3])
    foreach ($taskComponent in $taskComponents) {
        if ([long]$taskComponent -gt 65534) { throw 'Version components must be at most 65534 for Windows file versions.' }
    }
    return [pscustomobject]@{ Version = $Version; FileVersion = ($taskComponents -join '.') + '.0' }
}

function Reset-PackageDirectory {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Root)
    $taskRootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $taskDirectory = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $taskDirectory.StartsWith($taskRootPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Build directory must be inside $taskRootPath."
    }
    # Refuse redirected directories before recursively replacing build output.
    for ($taskParent = $taskDirectory; $taskParent.Length -ge $taskRootPath.Length; $taskParent = [IO.Path]::GetDirectoryName($taskParent)) {
        if ((Test-Path -LiteralPath $taskParent) -and ((Get-Item -LiteralPath $taskParent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Build directory is redirected: $taskParent"
        }
        if ($taskParent -eq $taskRootPath) { break }
    }
    foreach ($taskProcess in (Get-Process -Name 'RepoBackup','RepoBackup.Cli','RepoBackup.Runner' -ErrorAction SilentlyContinue)) {
        $taskProcessPath = $null
        try { $taskProcessPath = $taskProcess.Path } catch { }
        if ($taskProcessPath -and $taskProcessPath.StartsWith($taskDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Repo Backup is running from $taskDirectory. Close it or select a different -PackageDirectory before publishing."
        }
    }
    if (Test-Path -LiteralPath $taskDirectory) { Remove-Item -LiteralPath $taskDirectory -Recurse -Force }
    New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null
}

function Test-PackageManifest {
    param([Parameter(Mandatory)][string]$Path)
    $taskPackage = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $taskManifest = Get-Content -LiteralPath (Join-Path $taskPackage 'package-manifest.json') -Raw | ConvertFrom-Json
    $null = Get-PackageVersion -Version $taskManifest.version
    if ($taskManifest.architecture -ne 'win-x64' -or $taskManifest.files.Count -eq 0) { throw 'Invalid package manifest.' }
    $taskPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($taskFile in $taskManifest.files) {
        if ([IO.Path]::IsPathRooted($taskFile.path) -or $taskFile.path.Contains(':')) { throw 'Invalid package path.' }
        $taskFullPath = [IO.Path]::GetFullPath((Join-Path $taskPackage $taskFile.path))
        if (-not $taskFullPath.StartsWith($taskPackage + '\', [StringComparison]::OrdinalIgnoreCase) -or -not $taskPaths.Add($taskFullPath)) {
            throw 'Invalid or duplicate package path.'
        }
        if ((Get-FileHash -LiteralPath $taskFullPath -Algorithm SHA256).Hash -ne $taskFile.sha256) { throw "Package verification failed: $($taskFile.path)" }
    }
    foreach ($taskRequired in @('RepoBackup.exe', 'RepoBackup.Cli.exe', 'RepoBackup.Runner.exe', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'e_sqlite3.dll', 'restic\restic.exe', 'restic\LICENSE', 'git\cmd\git.exe', 'git\LICENSE.txt', 'git\cmd\git-lfs.exe', 'git\lfs\LICENSE')) {
        if (-not $taskPaths.Contains((Join-Path $taskPackage $taskRequired))) { throw "Package dependency missing: $taskRequired" }
    }
    foreach ($taskApp in @('RepoBackup', 'RepoBackup.Cli', 'RepoBackup.Runner')) {
        $taskRuntime = Get-Content -LiteralPath (Join-Path $taskPackage "$taskApp.runtimeconfig.json") -Raw | ConvertFrom-Json
        if (-not $taskRuntime.runtimeOptions.PSObject.Properties['includedFrameworks'] -or
            $taskRuntime.runtimeOptions.PSObject.Properties['framework'] -or $taskRuntime.runtimeOptions.PSObject.Properties['frameworks']) {
            throw "Package app is not self-contained: $taskApp"
        }
    }
    return $taskManifest
}

function Write-ReleaseChecksums {
    param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string[]]$Files)
    $taskLines = foreach ($taskFile in $Files) {
        $taskAsset = Join-Path $Directory $taskFile
        $taskHash = (Get-FileHash -LiteralPath $taskAsset -Algorithm SHA256).Hash.ToLowerInvariant()
        "$taskHash  $taskFile"
    }
    $taskLines | Set-Content -LiteralPath (Join-Path $Directory 'SHA256SUMS') -Encoding ASCII
}

Export-ModuleMember -Function Get-PackageVersion, Reset-PackageDirectory, Test-PackageManifest, Write-ReleaseChecksums
