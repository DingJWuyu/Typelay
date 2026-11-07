#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('help', 'restore', 'build', 'test', 'check', 'shared-check', 'format', 'pack', 'run', 'e2e', 'package-e2e', 'native-e2e', 'hy-adapter-prepare', 'hy-adapter-e2e', 'hy-runtime-prepare', 'hy-product-prepare')]
    [string]$Task = 'help',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$HyRuntime,
    [string]$IndexRuntime,
    [ValidateSet('ordinary-notepad', 'hy-production', 'hy-packages', 'index-windows')]
    [string]$Scenario
)
if ($Task -eq 'package-e2e' -and -not $Scenario) {
    throw 'package-e2e requires -Scenario ordinary-notepad, hy-production, hy-packages or index-windows.'
}
if ($Scenario -and $Task -ne 'package-e2e') {
    throw '-Scenario is only valid with package-e2e.'
}
if ($IndexRuntime -and $Task -notin @('pack', 'run')) {
    throw '-IndexRuntime is only valid with pack or run.'
}
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($HyRuntime -and $Task -notin @('pack', 'run')) {
    throw '-HyRuntime is only valid with pack or run.'
}
if ($Task -eq 'shared-check') {
    & (Join-Path $PSScriptRoot 'eng/shared/check.ps1') -Configuration $Configuration
} else {
    $arguments = @{ Task = $Task; Configuration = $Configuration; HyRuntime = $HyRuntime; IndexRuntime = $IndexRuntime }
    if ($Scenario) { $arguments.Scenario = $Scenario }
    & (Join-Path $PSScriptRoot 'eng/windows/dev.ps1') @arguments
}
