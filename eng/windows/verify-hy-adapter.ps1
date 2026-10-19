#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('prepare', 'run')]
    [string]$Phase,
    [ValidateSet('Release')]
    [string]$Configuration = 'Release'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$clock = [Diagnostics.Stopwatch]::StartNew()
if (-not $IsWindows -or -not [Environment]::Is64BitProcess) {
    throw 'Hy stage1 requires Windows x64. No model or SDK is installed by this entry point.'
}
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = Join-Path $root 'tests/windows/fixtures/TypeLay.NativeFixture/HyAdapter'
$artifacts = Join-Path $root 'artifacts'
$pythonCommand = Get-Command python.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
$python = [IO.Path]::GetFullPath($pythonCommand.Source)
if (-not (Test-Path -LiteralPath $python -PathType Leaf)) {
    throw 'The selected Python application does not exist.'
}

if ($Phase -eq 'prepare') {
    # Reserve ten seconds within the 600-second wall budget for terminating and
    # waiting for this owned preparation process tree. Hashing and SDK discovery
    # run inside it, so they cannot evade the outer deadline.
    $seconds = 590.0 - $clock.Elapsed.TotalSeconds
    if ($seconds -le 0) { throw 'Hy preparation budget expired before startup.' }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $python
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $root
    foreach ($argument in @('-I', '-B', (Join-Path $PSScriptRoot 'hy/prepare_assets.py'),
            '--artifacts', $artifacts, '--budget-seconds',
            $seconds.ToString('F3', [Globalization.CultureInfo]::InvariantCulture))) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        $started = $process.Start()
        if (-not $started) { throw 'Could not start owned Hy preparation.' }
        $milliseconds = [Math]::Max(1, [Math]::Floor((590.0 - $clock.Elapsed.TotalSeconds) * 1000))
        if (-not $process.WaitForExit([int]$milliseconds)) {
            $process.Kill($true)
            $cleanup = [Math]::Max(1, [Math]::Floor((600.0 - $clock.Elapsed.TotalSeconds) * 1000))
            if (-not $process.WaitForExit([int]$cleanup)) {
                throw 'Hy preparation deadline; owned preparation process did not signal exit.'
            }
            throw 'Hy preparation exceeded its 600-second budget including cleanup.'
        }
        if ($process.ExitCode -ne 0) { throw "Hy preparation failed with exit code $($process.ExitCode)." }
        if ($clock.Elapsed.TotalSeconds -gt 600) { throw 'Hy preparation exceeded its wall budget.' }
    } finally {
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            $cleanup = [Math]::Max(1, [Math]::Floor((600.0 - $clock.Elapsed.TotalSeconds) * 1000))
            if (-not $process.WaitForExit([int]$cleanup)) {
                Write-Error 'Owned Hy preparation cleanup did not finish within its wall budget.'
            }
        }
        $process.Dispose()
    }
} else {
    $fixture = Join-Path $root "tests/windows/fixtures/TypeLay.NativeFixture/bin/$Configuration/net10.0/TypeLay.NativeFixture.dll"
    if (-not (Test-Path -LiteralPath $fixture -PathType Leaf)) {
        throw 'Build the existing NativeFixture with ./dev.ps1 build before running Hy stage1.'
    }
    # The existing fixture owns the 180-second run deadline, all request/stdout
    # bounds, suspended creation, Job limits, cancellation and confirmed reaping.
    & dotnet $fixture '--verify-hy-adapter' $python (Join-Path $source 'hy_worker.py') `
        (Join-Path $artifacts 'hy-download') (Join-Path $artifacts 'hy-runtime') `
        (Join-Path $artifacts 'hy-build/typelay_hy.dll') (Join-Path $artifacts 'hy-results')
    if ($LASTEXITCODE -ne 0) { throw "Hy stage1 failed with exit code $LASTEXITCODE." }
}
