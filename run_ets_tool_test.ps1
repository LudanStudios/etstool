[CmdletBinding()]
param(
    [switch]$Build,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$groups = @(& whoami.exe /groups 2>$null)
$isLowIntegrity = ($groups -join "`n") -match 'S-1-16-4096'
if ($isLowIntegrity) {
    throw 'This shell is Low integrity and cannot safely refresh a normal-user test copy. Run this script from a normal Explorer/PowerShell session; it will start the test app after refreshing the copy.'
}

$source = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$programs = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs')).TrimEnd('\')
$runtime = [IO.Path]::GetFullPath((Join-Path $programs 'ETS-Assistant-Test-FullDriver')).TrimEnd('\')
$testRuntimeRoots = @(
    $runtime,
    [IO.Path]::GetFullPath((Join-Path $programs 'ETS-Assistant-Test')).TrimEnd('\')
)
$sourceExe = Join-Path $source '_winui\ETS-Assistant-WinUI.exe'
$runtimeExe = Join-Path $runtime '_winui\ETS-Assistant-WinUI.exe'
$sourceEngine = Join-Path $source 'ETS-Assistant-Engine.exe'
$runtimeEngine = Join-Path $runtime 'ETS-Assistant-Engine.exe'

if ($runtime -ne (Join-Path $programs 'ETS-Assistant-Test-FullDriver') -or
    $runtime -eq $source -or $runtime.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $source.StartsWith($runtime + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe test runtime path: $runtime"
}
foreach ($path in @($sourceExe, $sourceEngine, (Join-Path $source '_internal_engine'))) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing runtime component: $path" }
}

$processes = @(Get-CimInstance Win32_Process -ErrorAction Stop)
$runningUi = @($processes | Where-Object { $_.ExecutablePath -ieq $runtimeExe })
if ($runningUi.Count) {
    throw 'Close the running ETS Assistant test window before rebuilding or refreshing the test copy.'
}

if ($Build) {
    & (Join-Path $source 'build_ets_assistant_winui3.ps1') -SkipListenerSetup
    if (-not $?) { throw 'The build failed; the test copy was not updated.' }
} else {
    $changedSources = @(& git -C $source diff --name-only HEAD -- winui3 ets_activity.py ets_answer_listener.py ets_assistant.py ets_auto_answer.py ets_speaking_auto.py exam_pack_to_markdown_pdf.py)
    $untrackedSources = @(& git -C $source ls-files --others --exclude-standard -- winui3 '*.py')
    if ($LASTEXITCODE -ne 0) { throw 'Could not check source changes.' }
    if ($changedSources.Count -or $untrackedSources.Count) {
        throw 'Source code changed since the bundled binaries were built. Run this script with -Build before testing.'
    }
}

$processes = @(Get-CimInstance Win32_Process -ErrorAction Stop)
$running = @($processes | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath.StartsWith($runtime + '\', [StringComparison]::OrdinalIgnoreCase)
})
$listenerProcesses = @($running | Where-Object { $_.ExecutablePath -ieq $runtimeEngine }) +
    @($processes | Where-Object {
        $_.ExecutablePath -ieq $sourceEngine -and $_.CommandLine -like '*--background-listener*'
    })
foreach ($process in $listenerProcesses) {
    Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
    Wait-Process -Id $process.ProcessId -Timeout 5 -ErrorAction SilentlyContinue
}
$otherRunning = @($running | Where-Object {
    $candidateProcessId = $_.ProcessId
    -not @($listenerProcesses | Where-Object { $_.ProcessId -eq $candidateProcessId }).Count
})
if ($otherRunning.Count) {
    throw 'Another test runtime process is still running. Close it before refreshing the test copy.'
}

if (Test-Path -LiteralPath $runtime) {
    $item = Get-Item -LiteralPath $runtime -Force
    if ($item.FullName.TrimEnd('\') -ne $runtime -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to replace an unexpected or linked test directory: $runtime"
    }
    Remove-Item -LiteralPath $runtime -Recurse -Force
}
New-Item -ItemType Directory -Path $runtime -Force | Out-Null

$exclude = @('.git', '.build', '.build-tools', 'archive', 'temp', 'winui3', 'backend')
foreach ($item in Get-ChildItem -LiteralPath $source -Force) {
    if ($item.Name -in $exclude) { continue }
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Refusing to copy a linked source item: $($item.FullName)"
    }
    Copy-Item -LiteralPath $item.FullName -Destination $runtime -Recurse -Force
}

foreach ($pair in @(@($sourceExe, $runtimeExe), @($sourceEngine, $runtimeEngine))) {
    if (-not (Test-Path -LiteralPath $pair[1] -PathType Leaf) -or
        (Get-FileHash -LiteralPath $pair[0] -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $pair[1] -Algorithm SHA256).Hash) {
        throw "Test binary did not copy correctly: $($pair[1])"
    }
}

$shell = New-Object -ComObject WScript.Shell
$desktop = [Environment]::GetFolderPath('Desktop')
foreach ($shortcutName in @(
    'ETS Tool by @azursmelt.lnk',
    'ETS 自动监听助手.lnk',
    'ETS 自动监听助手（快速启动）.lnk'
)) {
    $shortcutPath = Join-Path $desktop $shortcutName
    if (-not (Test-Path -LiteralPath $shortcutPath)) { continue }
    $shortcut = $shell.CreateShortcut($shortcutPath)
    if ([string]::IsNullOrWhiteSpace($shortcut.TargetPath)) { continue }
    $target = [IO.Path]::GetFullPath($shortcut.TargetPath)
    $targetsTestRuntime = $false
    foreach ($testRoot in $testRuntimeRoots) {
        if ($target.StartsWith($testRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            $targetsTestRuntime = $true
            break
        }
    }
    if ($targetsTestRuntime) {
        # Remove shortcuts left by older script versions that made the test
        # copy appear as a second installed ETS Assistant app in search.
        Remove-Item -LiteralPath $shortcutPath -Force
    }
}

# Keep the user's existing login startup entry unchanged. Testing is started
# explicitly from this script and must not redirect normal startup to a test copy.

if ($listenerProcesses.Count -and -not $NoLaunch) {
    if ($isLowIntegrity) {
        Write-Warning 'The Low-integrity launcher cannot restart the background listener. The existing login startup entry is unchanged.'
    } else {
        $etsExe = 'C:\Program Files (x86)\ETS\Ets.exe'
        $arguments = '--background-listener --root "{0}" --config "{1}" --ets-exe "{2}" --debug-port 10086' -f
            (Join-Path $env:APPDATA 'ETS'), (Join-Path $runtime 'ets_answer_config.yaml'), $etsExe
        Start-Process -FilePath $runtimeEngine -ArgumentList $arguments -WorkingDirectory $runtime -WindowStyle Hidden
    }
}

Write-Output "Refreshed test runtime from $source"
Write-Output "Runtime: $runtime"
if (-not $NoLaunch) {
    if ($isLowIntegrity) {
        Write-Warning 'This launcher is Low integrity and cannot promote its child process. Run the script from a normal Explorer/PowerShell session to start the test app at normal integrity.'
    } else {
        Start-Process -FilePath $runtimeExe -WorkingDirectory (Split-Path -Parent $runtimeExe)
    }
}
