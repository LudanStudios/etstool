[CmdletBinding()]
param(
    [string]$EtsExe = 'C:\Program Files (x86)\ETS\Ets.exe',
    [int]$DebugPort = 10086
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'install_ets_answer_listener.ps1') -EtsExe $EtsExe -DebugPort $DebugPort
