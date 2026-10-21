$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath($PSScriptRoot)
$buildDir = Join-Path $root '.build'
$toolsDir = Join-Path $buildDir 'tools'
$nugetConfig = Join-Path $toolsDir 'nuget-restore\NuGet.Config'
$csproj = Join-Path $root 'winui3\ETSAssistant.WinUI3.csproj'
if (-not (Test-Path -LiteralPath $csproj -PathType Leaf)) { throw "WinUI project not found: $csproj" }
[xml]$projectXml = Get-Content -LiteralPath $csproj -Raw
$projectVersion = [string]$projectXml.Project.PropertyGroup.Version
if ($projectVersion -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Could not read a valid project version from $csproj."
}
$versionDir = Join-Path (Join-Path $buildDir 'versions') $projectVersion
$buildId = Get-Date -Format 'yyyyMMdd-HHmmss'
$stageRoot = Join-Path (Join-Path $versionDir 'staging') $buildId
$stageUi = Join-Path $stageRoot '_winui'
$stageEngine = Join-Path $stageRoot 'engine'
$stageDist = Join-Path $stageRoot 'pyinstaller'
$intermediateDir = Join-Path $versionDir 'intermediate'
$intermediateObj = Join-Path $intermediateDir 'obj'
$intermediateBin = Join-Path $intermediateDir 'bin'
$engineRuntime = '_internal_engine'
$uiRuntime = '_winui'
$targetEngine = Join-Path $root 'ETS-Assistant-Engine.exe'
$targetEngineRuntime = Join-Path $root $engineRuntime
$targetUi = Join-Path $root $uiRuntime
$archiveVersion = $projectVersion
$oldUiExe = Join-Path $targetUi 'ETS-Assistant-WinUI.exe'
if (Test-Path -LiteralPath $oldUiExe -PathType Leaf) {
    $oldProductVersion = (Get-Item -LiteralPath $oldUiExe).VersionInfo.ProductVersion
    if ($oldProductVersion -match '^(\d+\.\d+\.\d+)') { $archiveVersion = $Matches[1] }
}
$archive = Join-Path (Join-Path (Join-Path $buildDir 'versions') $archiveVersion) 'previous-builds'
New-Item -ItemType Directory -Force -Path $buildDir, $toolsDir, $versionDir, $stageRoot, $stageUi, $stageDist, $intermediateObj, $intermediateBin, $archive | Out-Null

$localDotnet = Join-Path $toolsDir 'dotnet'
if (Test-Path -LiteralPath (Join-Path $localDotnet 'dotnet.exe')) {
    $env:DOTNET_ROOT = $localDotnet
    $env:PATH = "$localDotnet;$env:PATH"
}
$sdkList = & dotnet --list-sdks 2>$null
if ($LASTEXITCODE -ne 0 -or -not $sdkList) {
    throw 'WinUI 3 build requires the .NET 8 SDK or later and the Windows App SDK NuGet package.'
}
$env:PYTHONPATH = if ($env:PYTHONPATH) { "$toolsDir;$env:PYTHONPATH" } else { $toolsDir }
python -c "import PyInstaller, PIL, reportlab" 2>$null
if ($LASTEXITCODE -ne 0) {
    throw 'Python build dependencies are missing. Install the packages listed in requirements-build.txt into .build\tools.'
}

$pyinstallerArgs = @(
    '-m', 'PyInstaller', '--clean', '--noconfirm', '--onedir',
    '--contents-directory', $engineRuntime, '--windowed',
    '--name', 'ETS-Assistant-Engine', '--distpath', $stageDist,
    '--workpath', (Join-Path $intermediateDir 'pyinstaller'), '--specpath', (Join-Path $intermediateDir 'specs'),
    '--icon', (Join-Path $root 'winui3\Assets\ProgramIcon.ico'),
    '--collect-all', 'reportlab', '--collect-all', 'PIL',
    '--add-data', "$($root)\README.md;.",
    (Join-Path $root 'ets_assistant.py')
)
python @pyinstallerArgs
if ($LASTEXITCODE -ne 0) { throw "Engine build failed with exit code $LASTEXITCODE." }

$stageEngineDir = Join-Path $stageDist 'ETS-Assistant-Engine'
$stageEngineExe = Join-Path $stageEngineDir 'ETS-Assistant-Engine.exe'
$stageEngineRuntime = Join-Path $stageEngineDir $engineRuntime
if (-not (Test-Path -LiteralPath $stageEngineExe -PathType Leaf) -or -not (Test-Path -LiteralPath $stageEngineRuntime -PathType Container)) {
    throw 'PyInstaller did not produce the engine and its runtime folder.'
}
$requiredEngineRuntimeFiles = @('_socket.pyd', '_multiprocessing.pyd')
foreach ($runtimeFile in $requiredEngineRuntimeFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $stageEngineRuntime $runtimeFile) -PathType Leaf)) {
        throw "PyInstaller runtime is incomplete: missing $runtimeFile from $stageEngineRuntime."
    }
}
if (-not (Get-ChildItem -LiteralPath $stageEngineRuntime -Filter 'python3*.dll' -File | Select-Object -First 1)) {
    throw "PyInstaller runtime is incomplete: missing the Python runtime DLL from $stageEngineRuntime."
}
if (Test-Path -LiteralPath (Join-Path $root '.git')) {
    # A complete local runtime can still produce a broken GitHub download if Git ignores its files.
    $runtimeRepositoryPaths = @(Get-ChildItem -LiteralPath $stageEngineRuntime -Recurse -File | ForEach-Object {
        $engineRuntime + '/' + $_.FullName.Substring($stageEngineRuntime.Length + 1).Replace('\', '/')
    })
    $ignoredRuntimePaths = @()
    for ($offset = 0; $offset -lt $runtimeRepositoryPaths.Count; $offset += 64) {
        $last = [Math]::Min($offset + 63, $runtimeRepositoryPaths.Count - 1)
        $batchPaths = $runtimeRepositoryPaths[$offset..$last]
        $ignoredRuntimePaths += @(& git -C $root check-ignore --no-index -- $batchPaths)
        if ($LASTEXITCODE -gt 1) { throw 'Could not verify the engine runtime against Git ignore rules.' }
    }
    if ($ignoredRuntimePaths.Count -gt 0) {
        throw ("Engine runtime files would be missing from GitHub downloads because Git ignores them: " + ($ignoredRuntimePaths -join ', '))
    }
}

$dotnetPublishArgs = @(
    'publish', $csproj, '--configuration', 'Release', '--runtime', 'win-x64',
    '--self-contained', 'true', '--output', $stageUi,
    "-p:BaseIntermediateOutputPath=$intermediateObj\",
    "-p:MSBuildProjectExtensionsPath=$intermediateObj\",
    "-p:BaseOutputPath=$intermediateBin\"
)
if (Test-Path -LiteralPath $nugetConfig -PathType Leaf) {
    $dotnetPublishArgs += "-p:RestoreConfigFile=$nugetConfig"
}
& dotnet @dotnetPublishArgs
if ($LASTEXITCODE -ne 0) { throw "WinUI 3 publish failed with exit code $LASTEXITCODE." }
$stageUiExe = Join-Path $stageUi 'ETS-Assistant-WinUI.exe'
if (-not (Test-Path -LiteralPath $stageUiExe -PathType Leaf)) { throw 'WinUI publish did not produce ETS-Assistant-WinUI.exe.' }
$stageUiPri = Join-Path $stageUi 'ETS-Assistant-WinUI.pri'
if (-not (Test-Path -LiteralPath $stageUiPri -PathType Leaf)) { throw 'WinUI 3 publish did not include its XAML resource index.' }

# Keep the VC++ runtime DLLs beside the unpackaged WinUI app in clean releases.
$vcRuntimeFiles = @(
    'concrt140.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll',
    'msvcp140_atomic_wait.dll', 'msvcp140_codecvt_ids.dll',
    'vcruntime140.dll', 'vcruntime140_1.dll', 'vcruntime140_threads.dll'
)
foreach ($runtimeFile in $vcRuntimeFiles) {
    $runtimeSource = Join-Path $targetUi $runtimeFile
    if (-not (Test-Path -LiteralPath $runtimeSource -PathType Leaf)) {
        throw \"Required VC++ runtime file not found in the current release: $runtimeSource\"
    }
    Copy-Item -LiteralPath $runtimeSource -Destination $stageUi -Force
}

$ownedExecutables = @($targetEngine, (Join-Path $targetUi 'ETS-Assistant-WinUI.exe'),
    (Join-Path $root 'ETS-Assistant-Fast.exe'), (Join-Path $root 'ETS-Assistant.exe'))
$running = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $ownedExecutables -contains $_.ExecutablePath })
$running = @($running | Where-Object { $_.CommandLine -like '*--background-listener*' }) +
           @($running | Where-Object { $_.CommandLine -notlike '*--background-listener*' })
foreach ($process in $running) { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue }
if ($running.Count) { $running | ForEach-Object { Wait-Process -Id $_.ProcessId -Timeout 5 -ErrorAction SilentlyContinue } }
foreach ($path in @($targetEngine, $targetEngineRuntime, $targetUi, $archive, $stageUi, $stageEngineRuntime)) {
    $resolved = [IO.Path]::GetFullPath($path)
    if (-not $resolved.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe deployment path: $resolved" }
    if (Test-Path -LiteralPath $resolved) {
        if ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing to replace a linked path: $resolved" }
    }
}
if (Test-Path -LiteralPath $targetEngine) {
    Move-Item -LiteralPath $targetEngine -Destination (Join-Path $archive ("ETS-Assistant-Engine-" + $buildId + '.exe'))
}
if (Test-Path -LiteralPath $targetEngineRuntime) {
    Move-Item -LiteralPath $targetEngineRuntime -Destination (Join-Path $archive ($engineRuntime + '-' + $buildId))
}
if (Test-Path -LiteralPath $targetUi) {
    Move-Item -LiteralPath $targetUi -Destination (Join-Path $archive ($uiRuntime + '-' + $buildId))
}
Move-Item -LiteralPath $stageEngineRuntime -Destination $targetEngineRuntime
Move-Item -LiteralPath $stageEngineExe -Destination $targetEngine
Move-Item -LiteralPath $stageUi -Destination $targetUi
$archivedUi = Join-Path $archive ($uiRuntime + '-' + $buildId)
$archivedCredential = Join-Path $archivedUi 'aliyun_tts_credentials.bin'
$currentCredential = Join-Path $targetUi 'aliyun_tts_credentials.bin'
if (Test-Path -LiteralPath $archivedCredential -PathType Leaf) {
    if (Test-Path -LiteralPath $currentCredential) { throw "Credential file already exists in the new program directory: $currentCredential" }
    Move-Item -LiteralPath $archivedCredential -Destination $currentCredential
}

& (Join-Path $root 'install_ets_answer_listener.ps1')
if (-not ('ETSAssistantBuild.IconCache' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
namespace ETSAssistantBuild {
    public static class IconCache {
        [DllImport("shell32.dll")]
        public static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
'@
}
[ETSAssistantBuild.IconCache]::SHChangeNotify(0x08000000, 0x1000, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Output "WinUI 3 assistant: $(Join-Path $targetUi 'ETS-Assistant-WinUI.exe')"
Write-Output "Background engine: $targetEngine"
Write-Output 'Updated login listener and desktop shortcuts; removed legacy assistant launchers.'
