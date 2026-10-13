#requires -Version 7.2
[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $PSScriptRoot 'commands.ps1')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:PYTHONDONTWRITEBYTECODE = '1'
$oldPythonPath = $env:PYTHONPATH
$env:PYTHONPATH = Join-Path $root 'src/shared/TypeLay.Worker'
$artifacts = Join-Path $root 'artifacts'

function Test-Shared {
    foreach ($project in @('tests/shared/TypeLay.Core.Tests', 'tests/shared/TypeLay.Runtime.Tests',
            'tests/shared/TypeLay.Protocol.Tests', 'tests/windows/TypeLay.Windows.Contracts.Tests')) {
        Invoke-Checked dotnet @('test', $project, '-c', $Configuration, '-p:RestoreLockedMode=true',
            '--logger', 'trx', '--results-directory', (Join-Path $artifacts 'shared-test-results'))
    }
    Invoke-Checked python @('-m', 'unittest', 'discover', '-s', 'tests/shared/worker', '-v')
}
Push-Location $root
try { Test-Shared }
finally {
    Pop-Location
    $env:PYTHONPATH = $oldPythonPath
}
