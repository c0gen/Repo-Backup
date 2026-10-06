param([string]$Version, [switch]$Installer, [string]$ReleaseDirectory)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'packaging\Package.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'packaging\SmokeTest.psm1') -Force
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskVersion = (Get-PackageVersion -Version $Version).Version
$taskRelease = if ($ReleaseDirectory) { [IO.Path]::GetFullPath($ReleaseDirectory) } else { Join-Path $taskRoot 'dist' }
$taskFixture = Join-Path $taskRoot ('.artifacts\package-smoke\' + [Guid]::NewGuid().ToString('N'))
$taskPortable = Join-Path $taskFixture 'portable'
Expand-Archive -LiteralPath (Join-Path $taskRelease "RepoBackup-$taskVersion-win-x64.zip") -DestinationPath $taskPortable
$taskManifest = Test-PackageManifest -Path $taskPortable
if ($taskManifest.version -ne $taskVersion) { throw 'Portable package version mismatch.' }
$null = Invoke-PackageSmokeTest -Package $taskPortable -Fixture (Join-Path $taskFixture 'portable-fixture')

if ($Installer) {
    $taskRegistry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8B229946-6D62-47D3-B682-7D5BFC20B7D0}_is1'
    $taskShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Repo Backup.lnk'
    # Run this part on a clean CI runner; never overwrite a real installation or shortcut.
    if ((Test-Path -LiteralPath $taskRegistry) -or (Test-Path -LiteralPath $taskShortcut)) { throw 'Installer smoke requires no existing Repo Backup installation or Start menu shortcut. Run without -Installer or use a clean Windows runner.' }
    $taskInstall = Join-Path $taskFixture 'installed'
    $taskSetup = Join-Path $taskRelease "RepoBackup-$taskVersion-win-x64-setup.exe"
    $taskUninstall = Join-Path $taskInstall 'unins000.exe'
    $taskArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/TASKS=', "/DIR=$taskInstall", "/LOG=$(Join-Path $taskFixture 'install.log')")
    $taskNeedsUninstall = $false
    try {
        $taskMutex = [Threading.Mutex]::new($false, 'Local\RepoBackup.InUse')
        try {
            $taskBlocked = $false
            try { $null = Invoke-SmokeProcess -Executable $taskSetup -Arguments $taskArguments }
            catch { $taskBlocked = $true }
            if (-not $taskBlocked -or (Test-Path -LiteralPath $taskUninstall)) { throw 'Installer replaced files while an app lifetime mutex existed.' }
        }
        finally { $taskMutex.Dispose() }
        $taskNeedsUninstall = $true
        $null = Invoke-SmokeProcess -Executable $taskSetup -Arguments $taskArguments
        if (-not (Test-Path -LiteralPath $taskRegistry) -or -not (Test-Path -LiteralPath $taskShortcut)) { throw 'Installer did not register an uninstaller and Start menu shortcut.' }
        $null = Test-PackageManifest -Path $taskInstall
        $taskInstalledData = Invoke-PackageSmokeTest -Package $taskInstall -Fixture (Join-Path $taskFixture 'installed-fixture')
        $taskCatalog = Join-Path $taskInstalledData 'catalog.db'
        $taskCatalogHash = (Get-FileHash -LiteralPath $taskCatalog -Algorithm SHA256).Hash
        $null = Invoke-SmokeProcess -Executable $taskSetup -Arguments $taskArguments
        if ((Get-FileHash -LiteralPath $taskCatalog -Algorithm SHA256).Hash -ne $taskCatalogHash) { throw 'Reinstall changed the catalog.' }
        $null = Invoke-SmokeProcess -Executable $taskUninstall -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
        $taskNeedsUninstall = $false
        if ((Test-Path -LiteralPath (Join-Path $taskInstall 'RepoBackup.exe')) -or (Test-Path -LiteralPath $taskRegistry) -or (Test-Path -LiteralPath $taskShortcut)) { throw 'Uninstall left program files, its registration, or its shortcut.' }
        if ((Get-FileHash -LiteralPath $taskCatalog -Algorithm SHA256).Hash -ne $taskCatalogHash) { throw 'Uninstall changed the catalog.' }
        if (-not (Test-Path -LiteralPath (Join-Path $taskFixture 'installed-fixture\backup\config'))) { throw 'Uninstall removed the backup repository.' }
        Write-Host 'PASS installer running-app guard, installation, reinstall, and uninstall preserving catalog and backups.'
    }
    finally {
        if ($taskNeedsUninstall -and (Test-Path -LiteralPath $taskUninstall)) { $null = Invoke-SmokeProcess -Executable $taskUninstall -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') }
    }
}
Write-Host "Package smoke artifacts: $taskFixture"
