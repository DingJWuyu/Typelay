# Run from a normal, unlocked, interactive Windows desktop for -Native.
# This script never downloads models, enables a global hook or starts a resident client.
[CmdletBinding()]
param([switch]$Native)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$runId = [guid]::NewGuid().ToString('N')
$out = Join-Path $root ('evidence/runs/' + (Get-Date -Format 'yyyyMMddTHHmmss') + '-' + $runId.Substring(0,8))
New-Item -ItemType Directory -Force -Path $out | Out-Null
$stages = @()
$overall = 'not-run'
$failure = 0
function Get-SourceIdentity {
    $files = Get-ChildItem $root -Recurse -File | Where-Object {
        $relative = $_.FullName.Substring($root.Length + 1).Replace('\','/')
        $relative -notmatch '(^|/)(bin|obj|\.git|__pycache__)/' -and
        $relative -notmatch '^(evidence/runs/|artifacts/|models/)'
    }
    return @($files | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($root.Length + 1).Replace('\','/'); sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}
function Invoke-DotnetStage([string]$Name, [string[]]$CommandArgs) {
    $path = Join-Path $out ($Name + '.log')
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & dotnet @CommandArgs 2>&1 | Out-File -Encoding utf8 $path
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previous }
    [pscustomobject]@{ name = $Name; status = $(if ($code -eq 0) {'passed'} elseif ($code -eq 2) {'blocked'} else {'failed'}); exit_code = $code; log = ($Name + '.log') }
}
$before = Get-SourceIdentity
$before | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 (Join-Path $out 'source-before.json')
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        $stages += [pscustomobject]@{name='sdk';status='not-run';reason='dotnet-sdk-not-found'}
        $failure = 2
    } else {
        $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
        $env:DOTNET_NOLOGO='1'
        & dotnet --info 2>&1 | Out-File -Encoding utf8 (Join-Path $out 'dotnet-info.log')
        $build = Invoke-DotnetStage 'build' @('build','TypeLay.slnx','-c','Release','--nologo')
        $stages += $build
        if ($build.exit_code -ne 0) { $overall='failed';$failure=1 }
        else {
            $core = Invoke-DotnetStage 'core' @('tests/TypeLay.Core.Tests/bin/Release/net10.0/TypeLay.Core.Tests.dll')
            $stages += $core
            if ($core.exit_code -ne 0) { $overall='failed';$failure=1 }
            else { $overall='requested-subset-passed' }
            if ($Native -and $core.exit_code -eq 0) {
                $nativeResult = Invoke-DotnetStage 'native' @('tests/TypeLay.Windows.Tests/bin/Release/net10.0/TypeLay.Windows.Tests.dll', 'fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll')
                $stages += $nativeResult
                if ($nativeResult.exit_code -ne 0) { $overall=$nativeResult.status;$failure=$nativeResult.exit_code }
            } elseif (-not $Native) {
                $stages += [pscustomobject]@{name='native';status='not-run';reason='not-requested'}
            }
        }
    }
} catch {
    $overall='blocked';$failure=2
    $stages += [pscustomobject]@{name='runner';status='blocked';error_type=$_.Exception.GetType().Name}
} finally {
    $after = Get-SourceIdentity
    $after | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 (Join-Path $out 'source-after.json')
    $unchanged = (($before | ConvertTo-Json -Depth 8 -Compress) -eq ($after | ConvertTo-Json -Depth 8 -Compress))
    if (-not $unchanged) { $overall='failed';$failure=1 }
    [ordered]@{run_id=$runId;status=$overall;source_unchanged=$unchanged;native_requested=[bool]$Native;stages=$stages;
        not_proven=@('physical-Tab','real-IME','WPF-edit-focus','browser-UIA','local-model','production-atomicity','MVP-release')
    } | ConvertTo-Json -Depth 10 | Set-Content -Encoding UTF8 (Join-Path $out 'run.json')
    Write-Host "Evidence: $out"
    Write-Host "Result: $overall (this is not an MVP release acceptance)"
}
exit $failure
