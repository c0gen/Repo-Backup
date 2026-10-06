Set-StrictMode -Version Latest

function Invoke-SmokeProcess {
    param([Parameter(Mandatory)][string]$Executable, [string[]]$Arguments = @())
    $taskStart = New-Object Diagnostics.ProcessStartInfo
    $taskStart.FileName = $Executable
    $taskStart.UseShellExecute = $false
    $taskStart.CreateNoWindow = $true
    $taskStart.RedirectStandardOutput = $true
    $taskStart.RedirectStandardError = $true
    $taskStart.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $taskStart.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    # Windows argv escaping, including quotes and trailing backslashes. This also
    # works in Windows PowerShell 5.1, whose ProcessStartInfo has no ArgumentList.
    $taskStart.Arguments = (@($Arguments | ForEach-Object {
        $taskArgument = [regex]::Replace($_, '(\\*)"', '$1$1\"')
        '"' + [regex]::Replace($taskArgument, '(\\+)$', '$1$1') + '"'
    }) -join ' ')
    $taskProcess = [Diagnostics.Process]::Start($taskStart)
    try {
        $taskOutput = $taskProcess.StandardOutput.ReadToEndAsync()
        $taskError = $taskProcess.StandardError.ReadToEndAsync()
        if (-not $taskProcess.WaitForExit(120000)) { $taskProcess.Kill(); throw "Process timed out: $Executable" }
        if ($taskProcess.ExitCode -ne 0) { throw "$Executable failed (exit $($taskProcess.ExitCode)): $($taskError.Result) $($taskOutput.Result)" }
        return $taskOutput.Result.Trim()
    }
    finally { $taskProcess.Dispose() }
}

function Invoke-PackageSmokeTest {
    param([Parameter(Mandatory)][string]$Package, [Parameter(Mandatory)][string]$Fixture)
    New-Item -ItemType Directory -Path $Fixture -Force | Out-Null
    $taskSavedEnvironment = @{}
    $taskGitVariables = @('GIT_DIR', 'GIT_COMMON_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES', 'GIT_CONFIG_COUNT', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_SYSTEM')
    foreach ($taskVariable in (@('PATH', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_MULTILEVEL_LOOKUP') + $taskGitVariables)) {
        $taskSavedEnvironment[$taskVariable] = [Environment]::GetEnvironmentVariable($taskVariable)
    }
    try {
        # Apphost must load the bundled runtime; Git must resolve beside the app.
        # Keep Windows tools available for discovery callbacks and the hidden runner.
        $env:PATH = "$env:SystemRoot\System32;$env:SystemRoot"
        $env:DOTNET_ROOT = Join-Path $Fixture 'no-system-dotnet'
        $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
        $env:DOTNET_MULTILEVEL_LOOKUP = '0'
        foreach ($taskVariable in $taskGitVariables) { [Environment]::SetEnvironmentVariable($taskVariable, $null) }
        $taskGitConfig = Join-Path $Fixture 'empty-gitconfig'
        [IO.File]::WriteAllText($taskGitConfig, '')
        $env:GIT_CONFIG_GLOBAL = $taskGitConfig
        $env:GIT_CONFIG_SYSTEM = $taskGitConfig
        $taskGit = Join-Path $Package 'git\cmd\git.exe'
        function Invoke-FixtureGit {
            param([string[]]$Arguments)
            # Fixture setup writes canonical pointers directly. Only application
            # recovery should exercise the actual LFS clean filter below.
            Invoke-SmokeProcess -Executable $taskGit -Arguments (@('-c', 'filter.lfs.process=', '-c', 'filter.lfs.clean=', '-c', 'filter.lfs.smudge=', '-c', 'filter.lfs.required=false', '-c', 'core.autocrlf=false', '-c', "core.hooksPath=$(Join-Path $Fixture 'empty-template')", '-c', 'commit.gpgsign=false') + $Arguments)
        }
        $taskCli = Join-Path $Package 'RepoBackup.Cli.exe'
        $taskSource = Join-Path $Fixture 'source'
        $taskWorktree = Join-Path $Fixture 'worktree'
        $taskData = Join-Path $Fixture 'catalog'
        $taskBackup = Join-Path $Fixture 'backup'
        $taskRestore = Join-Path $Fixture 'restored'
        $taskTemplate = Join-Path $Fixture 'empty-template'
        New-Item -ItemType Directory -Path $taskTemplate -Force | Out-Null
        $null = Invoke-FixtureGit -Arguments @('init', '--initial-branch=main', "--template=$taskTemplate", $taskSource)
        $null = Invoke-FixtureGit -Arguments @('-C', $taskSource, 'config', 'user.name', 'Package Test')
        $null = Invoke-FixtureGit -Arguments @('-C', $taskSource, 'config', 'user.email', 'package-test@example.invalid')
        [IO.File]::WriteAllText((Join-Path $taskSource 'tracked.txt'), "committed content`n")
        # Build a local LFS fixture without a server or developer-machine Git LFS.
        $taskLfsContent = [Text.Encoding]::UTF8.GetBytes("package LFS asset`n")
        $taskSha = [Security.Cryptography.SHA256]::Create()
        try { $taskLfsHash = ([BitConverter]::ToString($taskSha.ComputeHash($taskLfsContent))).Replace('-', '').ToLowerInvariant() }
        finally { $taskSha.Dispose() }
        [IO.File]::WriteAllText((Join-Path $taskSource '.gitattributes'), "asset.bin filter=lfs diff=lfs merge=lfs -text`n")
        [IO.File]::WriteAllText((Join-Path $taskSource 'asset.bin'), "version https://git-lfs.github.com/spec/v1`noid sha256:$taskLfsHash`nsize $($taskLfsContent.Length)`n", [Text.UTF8Encoding]::new($false))
        $null = Invoke-FixtureGit -Arguments @('-C', $taskSource, 'add', '.')
        $null = Invoke-FixtureGit -Arguments @('-C', $taskSource, 'commit', '-m', 'Package fixture')
        $null = Invoke-FixtureGit -Arguments @('-C', $taskSource, 'worktree', 'add', '-b', 'smoke', $taskWorktree)
        $taskLfsStore = Join-Path $taskSource ('.git\lfs\objects\' + $taskLfsHash.Substring(0, 2) + '\' + $taskLfsHash.Substring(2, 2))
        New-Item -ItemType Directory -Path $taskLfsStore -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $taskLfsStore $taskLfsHash), $taskLfsContent)
        [IO.File]::WriteAllBytes((Join-Path $taskSource 'asset.bin'), $taskLfsContent)
        foreach ($taskFilter in @(@('filter.lfs.clean', 'git-lfs clean -- %f'), @('filter.lfs.smudge', 'git-lfs smudge -- %f'), @('filter.lfs.process', 'git-lfs filter-process'), @('filter.lfs.required', 'true'))) {
            $null = Invoke-FixtureGit -Arguments @('-C', $taskSource, 'config', $taskFilter[0], $taskFilter[1])
        }
        [IO.File]::WriteAllText((Join-Path $taskSource 'tracked.txt'), "staged content`n")
        $null = Invoke-FixtureGit -Arguments @('-C', $taskSource, 'add', 'tracked.txt')
        [IO.File]::WriteAllText((Join-Path $taskSource 'tracked.txt'), "unfinished content`n")
        [IO.File]::WriteAllText((Join-Path $taskSource '.env'), "FIXTURE_ONLY=package-smoke`n")
        [IO.File]::WriteAllText((Join-Path $taskWorktree 'worktree.txt'), "unfinished worktree`n")
        $taskProject = Invoke-SmokeProcess -Executable $taskCli -Arguments @('add-folder', '--path', $taskSource, '--name', 'Package smoke', '--data-dir', $taskData) | ConvertFrom-Json
        $taskDestination = Invoke-SmokeProcess -Executable $taskCli -Arguments @('destination-add', '--path', $taskBackup, '--name', 'Fixture backup', '--data-dir', $taskData) | ConvertFrom-Json
        $null = Invoke-SmokeProcess -Executable $taskCli -Arguments @('approve', '--project', $taskProject.id, '--data-dir', $taskData)
        $null = Invoke-SmokeProcess -Executable $taskCli -Arguments @('backup', '--destination', $taskDestination.id, '--all-enabled', '--data-dir', $taskData)
        $taskSnapshots = @(Invoke-SmokeProcess -Executable $taskCli -Arguments @('snapshots', '--destination', $taskDestination.id, '--data-dir', $taskData) | ConvertFrom-Json)
        if ($taskSnapshots.Count -ne 1) { throw 'Package smoke did not produce one recovery snapshot.' }
        $null = Invoke-SmokeProcess -Executable $taskCli -Arguments @('restore', '--destination', $taskDestination.id, '--snapshot', $taskSnapshots[0].id, '--target', $taskRestore, '--data-dir', $taskData)
        $taskRestoredFile = @(Get-ChildItem -LiteralPath $taskRestore -Filter 'tracked.txt' -File -Recurse | Where-Object { [IO.File]::ReadAllText($_.FullName) -eq "unfinished content`n" })
        if ($taskRestoredFile.Count -ne 1) { throw 'Restored working file differs from the original.' }
        if (@(Get-ChildItem -LiteralPath $taskRestore -Filter '.env' -Force -File -Recurse).Count -ne 1) { throw 'Untracked configuration was not restored.' }
        if (@(Get-ChildItem -LiteralPath $taskRestore -Filter 'worktree.txt' -File -Recurse).Count -ne 1) { throw 'Linked worktree was not restored.' }
        $taskRestoredAsset = @(Get-ChildItem -LiteralPath $taskRestore -Filter 'asset.bin' -File -Recurse | Where-Object { [IO.File]::ReadAllText($_.FullName) -eq "package LFS asset`n" })
        if ($taskRestoredAsset.Count -ne 1 -or @(Get-ChildItem -LiteralPath $taskRestore -Filter $taskLfsHash -Force -File -Recurse).Count -eq 0) { throw 'Local LFS content was not restored.' }
        $null = Invoke-SmokeProcess -Executable (Join-Path $Package 'RepoBackup.Runner.exe') -Arguments @('projects', '--data-dir', $taskData)
        $taskPreview = Join-Path $Fixture 'desktop.png'
        $null = Invoke-SmokeProcess -Executable (Join-Path $Package 'RepoBackup.exe') -Arguments @('--data-dir', $taskData, '--render-preview', $taskPreview, '--synthetic-preview')
        if (-not (Test-Path -LiteralPath $taskPreview)) { throw 'Packaged desktop did not render.' }
        Write-Host 'PASS packaged CLI, backup/restore with linked worktree and local LFS data, hidden runner, and WPF desktop without system Git/.NET discovery.'
        return $taskData
    }
    finally {
        foreach ($taskVariable in $taskSavedEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($taskVariable, $taskSavedEnvironment[$taskVariable])
        }
    }
}

Export-ModuleMember -Function Invoke-SmokeProcess, Invoke-PackageSmokeTest
