# Removes generated speaking/TTS audio from the program's temp folder.
# Keeps speaking_session.json unless -IncludeSession is supplied.
[CmdletBinding()]
param(
    [string]$Root = $PSScriptRoot,
    [switch]$IncludeSession,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$rootPath = [IO.Path]::GetFullPath($Root)
$removed = 0
$bytes = 0

function Remove-AudioFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    try {
        $length = (Get-Item -LiteralPath $Path).Length
        Remove-Item -LiteralPath $Path -Force
        $script:removed++
        $script:bytes += $length
    } catch {
        if (-not $Quiet) { Write-Warning "Could not remove $Path : $($_.Exception.Message)" }
    }
}

$ttsFolder = Join-Path $rootPath 'temp\tts-test'
if (Test-Path -LiteralPath $ttsFolder) {
    foreach ($file in Get-ChildItem -LiteralPath $ttsFolder -Filter '*.wav' -File -ErrorAction SilentlyContinue) {
        Remove-AudioFile $file.FullName
    }
}

# The single-file virtual microphone copy, if a TTS WAV was prepared for it.
$speakingInput = Join-Path $env:APPDATA 'ETS\_assistant\speaking_input.wav'
Remove-AudioFile $speakingInput

if ($IncludeSession) {
    foreach ($session in @(
        (Join-Path $rootPath 'temp\speaking_session.json'),
        (Join-Path $env:APPDATA 'ETS\_assistant\speaking_session.json')
    )) {
        if (Test-Path -LiteralPath $session -PathType Leaf) {
            Remove-Item -LiteralPath $session -Force -ErrorAction SilentlyContinue
        }
    }
}

if (-not $Quiet) {
    $size = if ($bytes -ge 1MB) { '{0:N1} MB' -f ($bytes / 1MB) } else { '{0:N0} KB' -f ($bytes / 1KB) }
    Write-Output "Removed $removed audio file(s), $size."
}
