#requires -Version 5.1
[CmdletBinding()]
param([switch]$NativeFixture)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_NOLOGO='1'
if (-not [Environment]::Is64BitOperatingSystem -or $env:OS -ne 'Windows_NT') { throw 'Windows x64 is required.' }
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$out = Join-Path $root ('artifacts\validation-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $out | Out-Null
function Run([string]$name, [string[]]$arguments) {
    & $dotnet @arguments 2>&1 | Tee-Object -FilePath (Join-Path $out ($name+'.log')) | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$name failed with exit $LASTEXITCODE. See $out" }
}
Push-Location $root
try {
    Run 'sdk' @('--info')
    Run 'build' @('build','TypeLay.slnx','-c','Release','--nologo','-p:UseSharedCompilation=false','-p:TargetPlatformDisplayName=Windows')
    Run 'core' @('run','--project','tests/TypeLay.Core.Tests','-c','Release','--no-build')
    Run 'integration' @('run','--project','tests/TypeLay.Integration.Tests','-c','Release','--no-build')
    $native='not-run'
    if ($NativeFixture) {
        # Requires an unlocked interactive desktop. This exercises ONLY the M1 native fixture adapter.
        $fixture=Join-Path $root 'fixtures\TypeLay.NativeFixture\bin\Release\net10.0\TypeLay.NativeFixture.dll'
        Run 'native-fixture' @('run','--project','tests/TypeLay.Windows.Tests','-c','Release','--no-build','--',$fixture)
        $native='passed'
    }
    @{schema=1;build='passed';logic='passed';nativeFixture=$native;edge='not-run';physicalTab='not-run';ime='not-run';modelInference='not-run';at=[DateTime]::UtcNow.ToString('o')} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'result.json') -Encoding UTF8
    Write-Host "Validation completed. This does not certify browser, physical Tab, IME, or the first usable loop. $out"
} finally { Pop-Location }
