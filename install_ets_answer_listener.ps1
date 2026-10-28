[CmdletBinding()]
param(
    [string]$EtsExe = 'C:\Program Files (x86)\ETS\Ets.exe',
    [int]$DebugPort = 10086
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$testRuntimeRoots = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\ETS-Assistant-Test-FullDriver'),
    (Join-Path $env:LOCALAPPDATA 'Programs\ETS-Assistant-Test')
) | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') }
function Test-IsTestRuntimeTarget([string]$Target) {
    if ([string]::IsNullOrWhiteSpace($Target)) { return $false }
    try { $targetPath = [IO.Path]::GetFullPath($Target) }
    catch { return $false }
    foreach ($testRoot in $testRuntimeRoots) {
        if ($targetPath.StartsWith($testRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}
$packRoot = Join-Path $env:APPDATA 'ETS'
$config = Join-Path $root 'ets_answer_config.yaml'
$launcher = Join-Path $root 'ETS-Assistant-Engine.exe'
$runtime = Join-Path $root '_internal_engine'
$ui = Join-Path $root '_winui\ETS-Assistant-WinUI.exe'
$icon = Join-Path $root '_winui\Assets\ProgramIcon.ico'
$EtsExe = [IO.Path]::GetFullPath($EtsExe)
foreach ($path in @($launcher, $runtime, $ui, $icon, $config, $EtsExe)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required file or folder was not found: $path" }
}

# An already running legacy listener holds the same lock as the new engine.
$legacyPaths = @('ETS-Assistant-Fast.exe', 'ETS-Assistant.exe') | ForEach-Object { Join-Path $root $_ }
$legacy = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $legacyPaths -contains $_.ExecutablePath })
$legacy = @($legacy | Where-Object { $_.CommandLine -like '*--background-listener*' }) +
          @($legacy | Where-Object { $_.CommandLine -notlike '*--background-listener*' })
foreach ($process in $legacy) { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue }
if ($legacy.Count) { $legacy | ForEach-Object { Wait-Process -Id $_.ProcessId -Timeout 5 -ErrorAction SilentlyContinue } }

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-Item -Path $runKey -Force | Out-Null
foreach ($property in (Get-ItemProperty $runKey).PSObject.Properties) {
    if ($property.Value -is [string] -and $property.Name -ne 'ETS Answer Listener') {
        foreach ($legacyPath in $legacyPaths) {
            if ($property.Value.IndexOf($legacyPath, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                Remove-ItemProperty -Path $runKey -Name $property.Name
                break
            }
        }
    }
}
$arguments = '--background-listener --root "{0}" --config "{1}" --ets-exe "{2}" --debug-port {3}' -f $packRoot, $config, $EtsExe, $DebugPort
New-ItemProperty -Path $runKey -Name 'ETS Answer Listener' -PropertyType String -Value ('"{0}" {1}' -f $launcher, $arguments) -Force | Out-Null

$shell = New-Object -ComObject WScript.Shell
$desktop = [Environment]::GetFolderPath('Desktop')
$startup = [Environment]::GetFolderPath('Startup')
function Set-AssistantShortcut([string]$Path, [string]$Target, [string]$Arguments, [string]$Icon) {
    $link = $shell.CreateShortcut($Path)
    $link.TargetPath = $Target
    $link.Arguments = $Arguments
    $link.WorkingDirectory = Split-Path -Parent $Target
    $link.IconLocation = $Icon
    $link.WindowStyle = 1
    $link.Save()
}
Set-AssistantShortcut (Join-Path $desktop 'ETS 自动监听助手.lnk') $ui '' ($icon + ',0')
Set-AssistantShortcut (Join-Path $desktop 'ETS 自动监听助手（快速启动）.lnk') $ui '' ($icon + ',0')
Set-AssistantShortcut (Join-Path $desktop 'ETS.lnk') $EtsExe ("--remote-debugging-port=$DebugPort") ($EtsExe + ',0')

# Login starts only the background engine. ETS detection opens the WinUI window.
$startupLink = Join-Path $startup 'ETS Assistant.lnk'
if (Test-Path -LiteralPath $startupLink) {
    $link = $shell.CreateShortcut($startupLink)
    if ($link.TargetPath -ieq $ui -or $legacyPaths -contains $link.TargetPath -or
        (Test-IsTestRuntimeTarget $link.TargetPath)) { Remove-Item -LiteralPath $startupLink }
}
$oldDesktopLink = Join-Path $desktop 'ETS Answer Listener.lnk'
if (Test-Path -LiteralPath $oldDesktopLink) {
    $link = $shell.CreateShortcut($oldDesktopLink)
    if ($link.TargetPath -ieq $EtsExe -or $legacyPaths -contains $link.TargetPath -or
        (Test-IsTestRuntimeTarget $link.TargetPath)) { Remove-Item -LiteralPath $oldDesktopLink }
}
$shortcutRoots = @($desktop, [Environment]::GetFolderPath('Programs'), (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned'))
foreach ($folder in $shortcutRoots) {
    if (-not (Test-Path -LiteralPath $folder)) { continue }
    foreach ($file in @(Get-ChildItem -LiteralPath $folder -Filter '*.lnk' -File -Recurse -Force -ErrorAction SilentlyContinue)) {
        $link = $shell.CreateShortcut($file.FullName)
        if (Test-IsTestRuntimeTarget $link.TargetPath) {
            # Retire stale test-copy shortcuts so Windows Search shows one app.
            Remove-Item -LiteralPath $file.FullName
        }
        elseif ($legacyPaths -contains $link.TargetPath) {
            if ($file.FullName.StartsWith($startup + '\', [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $file.FullName }
            else { Set-AssistantShortcut $file.FullName $ui '' ($icon + ',0') }
        }
    }
}

$running = @(Get-CimInstance Win32_Process -Filter "Name='ETS-Assistant-Engine.exe'" | Where-Object {
    $_.ExecutablePath -ieq $launcher -and $_.CommandLine -like '*--background-listener*'
})
if (-not $running.Count) {
    $process = Start-Process -FilePath $launcher -ArgumentList $arguments -WorkingDirectory $root -WindowStyle Hidden -PassThru
    if ($process.WaitForExit(1200)) { throw "The new listener exited during startup (exit code $($process.ExitCode))." }
}

foreach ($name in @('ETS-Assistant-Fast.exe', 'ETS-Assistant.exe', '_internal_fast')) {
    $oldPath = [IO.Path]::GetFullPath((Join-Path $root $name))
    if (-not $oldPath.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe cleanup path: $oldPath" }
    if (Test-Path -LiteralPath $oldPath) {
        $item = Get-Item -LiteralPath $oldPath
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing to remove a linked legacy path: $oldPath" }
        if ($item.PSIsContainer) { Remove-Item -LiteralPath $oldPath -Recurse -Force }
        else { Remove-Item -LiteralPath $oldPath -Force }
    }
}
Write-Output 'The WinUI listener is active. Legacy assistant processes, launchers, and runtime files have been removed.'
