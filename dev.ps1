#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('help', 'restore', 'build', 'test', 'check', 'shared-check', 'format', 'pack', 'run', 'e2e', 'native-e2e', 'hy-adapter-prepare', 'hy-adapter-e2e')]
    [string]$Task = 'help',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Task -eq 'shared-check') {
    & (Join-Path $PSScriptRoot 'eng/shared/check.ps1') -Configuration $Configuration
} else {
    & (Join-Path $PSScriptRoot 'eng/windows/dev.ps1') -Task $Task -Configuration $Configuration
}
