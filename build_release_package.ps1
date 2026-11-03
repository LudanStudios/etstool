[CmdletBinding()]
param(
    [string]$RuntimeRoot = $PSScriptRoot,
    [string]$AnswerConfigPath = (Join-Path $PSScriptRoot 'ets_answer_config.yaml'),
    [switch]$NoRestore,
    [switch]$SuppressValidation
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$runtimeRoot = [IO.Path]::GetFullPath($RuntimeRoot)
$csproj = Join-Path $root 'winui3\ETSAssistant.WinUI3.csproj'
[xml]$project = Get-Content -LiteralPath $csproj -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid release version: $version" }

$manifestPath = Join-Path $runtimeRoot 'MANIFEST.sha256'
$manifestRoots = @('ETS-Assistant-Engine.exe', '_internal_engine', '_winui', 'tools', 'VERSION.txt')
$excludedNames = @('aliyun_tts_credentials.bin', 'deepseek_api_key.txt', 'credentials.json', '.env')
$runtimeFiles = [Collections.Generic.List[IO.FileInfo]]::new()
foreach ($relativeRoot in $manifestRoots) {
    $path = Join-Path $runtimeRoot $relativeRoot
    if (-not (Test-Path -LiteralPath $path)) { throw "Release runtime path missing: $path" }
    $item = Get-Item -LiteralPath $path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing a linked release path: $path" }
    if ($item.PSIsContainer) {
        foreach ($file in Get-ChildItem -LiteralPath $path -Recurse -File -Force) {
            if ($file.Name.ToLowerInvariant() -in $excludedNames) { continue }
            if ($file.Extension -ieq '.pdb') { continue }
            if ($relativeRoot -eq '_internal_engine' -and $file.Name -ieq 'README.md') { continue }
            $runtimeFiles.Add($file)
        }
    } else {
        if ($item.Name.ToLowerInvariant() -notin $excludedNames) { $runtimeFiles.Add($item) }
    }
}
$manifestLines = foreach ($file in ($runtimeFiles | Sort-Object FullName)) {
    $relative = $file.FullName.Substring($runtimeRoot.Length + 1).Replace('\', '/')
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $relative"
}
[IO.File]::WriteAllLines($manifestPath, [string[]]$manifestLines, [Text.Encoding]::ASCII)

$releaseRoot = Join-Path (Join-Path (Join-Path $root '.build\versions') $version) 'release'
$buildId = Get-Date -Format 'yyyyMMdd-HHmmss'
$stageRoot = Join-Path (Join-Path $releaseRoot 'staging') $buildId
$payload = Join-Path $stageRoot 'payload'
$wixOutput = Join-Path $stageRoot 'wix'
$wixIntermediate = Join-Path $stageRoot 'obj'
New-Item -ItemType Directory -Force -Path $payload, $wixOutput, $wixIntermediate | Out-Null

foreach ($line in $manifestLines) {
    if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw "Invalid generated manifest line: $line" }
    $relative = $Matches[2].Replace('/', '\')
    $source = [IO.Path]::GetFullPath((Join-Path $runtimeRoot $relative))
    if (-not $source.StartsWith($runtimeRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe manifest path: $relative" }
    $destination = Join-Path $payload $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
}
foreach ($relative in @('README.md', 'llm_stt.md')) {
    $source = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Release support file missing: $source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $payload $relative)
}
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $payload 'MANIFEST.sha256')
if (-not (Test-Path -LiteralPath $AnswerConfigPath -PathType Leaf)) {
    throw "Sanitized answer configuration file missing: $AnswerConfigPath"
}
Copy-Item -LiteralPath $AnswerConfigPath -Destination (Join-Path $payload 'ets_answer_config.yaml')
$unexpectedCredential = Get-ChildItem -LiteralPath $payload -Recurse -File -Force | Where-Object { $_.Name.ToLowerInvariant() -in $excludedNames }
if ($unexpectedCredential) { throw 'A credential-like file was found in the release payload.' }

$installerProject = Join-Path $root 'Installer\ETS-Assistant.wixproj'
$buildStarted = Get-Date
$msiName = "ETS-Assistant-r$version.msi"
$zipName = "ETS-Assistant-r$version.zip"
$buildArgs = @('build', $installerProject, '--configuration', 'Release', "-p:ProductVersion=$version",
    "-p:PayloadDirectory=$payload", "-p:OutputPath=$wixOutput\", '-p:NuGetAudit=false', '--verbosity', 'minimal')
if ($NoRestore) { $buildArgs += '--no-restore' }
else { $buildArgs += @("-p:BaseIntermediateOutputPath=$wixIntermediate\", "-p:MSBuildProjectExtensionsPath=$wixIntermediate\", '-p:RestoreIgnoreFailedSources=true') }
if ($SuppressValidation) { $buildArgs += '-p:SuppressValidation=true' }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "WiX MSI build failed with exit code $LASTEXITCODE." }
$builtMsi = Join-Path $wixOutput $msiName
if (-not (Test-Path -LiteralPath $builtMsi -PathType Leaf)) {
    $searchRoots = @($wixOutput, (Join-Path $root 'Installer\bin'), (Join-Path $root 'Installer\obj')) | Where-Object { Test-Path -LiteralPath $_ }
$builtMsi = Get-ChildItem -LiteralPath $searchRoots -Recurse -Filter '*.msi' -File | Where-Object { $_.LastWriteTime -ge $buildStarted.AddSeconds(-2) } | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $builtMsi) { throw "WiX did not produce $msiName." }

# ICE03 cannot represent some valid Windows App SDK MUI language lists in MSI's File.Language column.
# This column is used for patch applicability, not runtime resource lookup; normalize that metadata to ProductLanguage.
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($builtMsi, 1)
$view = $database.OpenView("UPDATE ``File`` SET ``Language`` = '1033'")
$view.Execute()
$view.Close()
$database.Commit()
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
$verifyInstaller = New-Object -ComObject WindowsInstaller.Installer
$verifyDatabase = $verifyInstaller.OpenDatabase($builtMsi, 0)
$verifyView = $verifyDatabase.OpenView("SELECT ``File``, ``Language`` FROM ``File``")
$verifyView.Execute()
$fileRows = 0
while ($record = $verifyView.Fetch()) {
    $fileRows++
    if ($record.StringData(2) -ne '1033') { throw "Unexpected MSI file language metadata in row $($record.StringData(1))." }
}
$verifyView.Close()
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($verifyView)
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($verifyDatabase)
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($verifyInstaller)
if ($fileRows -eq 0) { throw 'The MSI File table is empty.' }

New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
$msiPath = Join-Path $releaseRoot $msiName
$zipPath = Join-Path $releaseRoot $zipName
Copy-Item -LiteralPath $builtMsi -Destination $msiPath -Force
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipArchive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try { $entryCount = @($zipArchive.Entries | Where-Object { -not $_.FullName.EndsWith('/') }).Count } finally { $zipArchive.Dispose() }
$fileCount = @(Get-ChildItem -LiteralPath $payload -Recurse -File -Force).Count
if ($entryCount -ne $fileCount) { throw "Portable ZIP entry count mismatch: expected $fileCount, got $entryCount." }

Get-Item -LiteralPath $msiPath, $zipPath | Select-Object FullName, Length
Get-FileHash -LiteralPath $msiPath, $zipPath -Algorithm SHA256 | Select-Object Path, Hash
Write-Output "Release package directory: $releaseRoot"
