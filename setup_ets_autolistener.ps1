[CmdletBinding()]
param(
    [string]$EtsExe = "C:\Program Files (x86)\ETS\Ets.exe",
    [int]$DebugPort = 10086
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$listener = Join-Path $root 'ets_answer_listener.py'
$worker = Join-Path $root 'ets_auto_answer.py'
$converter = Join-Path $root 'exam_pack_to_markdown_pdf.py'

foreach ($file in @($listener, $worker, $converter)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Missing required script: $file"
    }
}

$EtsExe = [IO.Path]::GetFullPath($EtsExe)
if (-not (Test-Path -LiteralPath $EtsExe -PathType Leaf)) {
    throw "ETS executable not found: $EtsExe. Install ETS in the standard directory or update ETS_PATHS in ets_answer_listener.py."
}
$expectedInstall = [IO.Path]::GetFullPath('C:\Program Files (x86)\ETS\Ets.exe')
if ($EtsExe -ine $expectedInstall) {
    throw "The current listener is configured for the standard ETS install directory. Install ETS at '$expectedInstall' or update ETS_PATHS in ets_answer_listener.py before setup."
}

$python = $null
$pyLauncher = Get-Command 'py.exe' -ErrorAction SilentlyContinue
if ($pyLauncher) {
    $candidate = & $pyLauncher.Source -3 -c 'import sys; print(sys.executable)' 2>$null
    if ($LASTEXITCODE -eq 0) { $python = ($candidate | Select-Object -Last 1).Trim() }
}
if (-not $python) {
    $pythonCommand = Get-Command 'python.exe' -ErrorAction SilentlyContinue
    if ($pythonCommand) { $python = $pythonCommand.Source }
}
if (-not $python -or -not (Test-Path -LiteralPath $python -PathType Leaf)) {
    throw 'Python 3.10 or newer was not found. Install Python for Windows, then run this script again.'
}

$versionText = & $python -c 'import sys; print(f"{sys.version_info.major}.{sys.version_info.minor}")' 2>$null
if ($LASTEXITCODE -ne 0 -or [version]$versionText -lt [version]'3.10') {
    throw "Python 3.10 or newer is required; found '$versionText'."
}
$pythonw = Join-Path (Split-Path -Parent $python) 'pythonw.exe'
if (-not (Test-Path -LiteralPath $pythonw -PathType Leaf)) {
    throw "pythonw.exe was not found beside $python. Install the standard Windows Python distribution."
}

# Register the hidden supervisor for the current Windows user at sign-in.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runCommand = '"{0}" "{1}"' -f $pythonw, $listener
New-ItemProperty -Path $runKey -Name 'ETS Answer Listener' -Value $runCommand -PropertyType String -Force | Out-Null

# Preserve the vendor shortcut and provide a separate launcher with the CEF debug switch.
$desktop = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktop 'ETS Answer Listener.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $EtsExe
$shortcut.Arguments = "--remote-debugging-port=$DebugPort"
$shortcut.WorkingDirectory = Split-Path -Parent $EtsExe
$shortcut.IconLocation = "$EtsExe,0"
$shortcut.Save()

# Start the supervisor now unless this installation already has one running.
$listenerRunning = $false
$canInspectProcesses = $true
try {
    $listenerRunning = [bool](Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
        $_.CommandLine -and $_.CommandLine.Contains($listener)
    } | Select-Object -First 1)
} catch {
    $canInspectProcesses = $false
    Write-Warning 'Could not inspect running processes; the startup entry is configured. Sign out and back in to start the listener.'
}
if ($canInspectProcesses -and -not $listenerRunning) {
    Start-Process -FilePath $pythonw -ArgumentList @("`"$listener`"") -WorkingDirectory $root -WindowStyle Hidden
}

Write-Host "Configured login startup: $runCommand"
Write-Host "Created ETS launcher: $shortcutPath"
Write-Host "Python $versionText is ready; no extra Python packages are needed for answer monitoring."
Write-Host 'Open ETS using the new shortcut. The listener requires the app to expose its CEF debugging endpoint on 127.0.0.1.'
try {
    Invoke-RestMethod -Uri "http://127.0.0.1:$DebugPort/json/version" -TimeoutSec 2 | Out-Null
    Write-Host "Debug endpoint is responding on port $DebugPort."
} catch {
    Write-Warning "Port $DebugPort is not responding yet. If ETS is open, close it and relaunch using the new shortcut. If the port still does not respond, this ETS build is not forwarding the CEF switch; this setup does not patch vendor program files."
}

& $python -c 'import reportlab, PIL' 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Optional: install reportlab and Pillow only if PDF output is needed; Markdown and answer display work without them.'
}
