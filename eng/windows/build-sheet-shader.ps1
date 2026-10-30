#requires -Version 7.2
# Produces an isolated candidate, not a product resource or rendering acceptance.
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Refusing to overwrite an earlier shader generation result.' }
New-Item -ItemType Directory -Path $output | Out-Null
$result = [ordered]@{
    kind = 'application-sheet-saturation-generation'
    sourceSha = $env:GITHUB_SHA
    imageOS = $env:ImageOS
    imageVersion = $env:ImageVersion
    status = 'not-started'
    compilerInvoked = $false
    productAcceptance = $false
    renderingValidated = $false
    sourceHash = $null
    compilerHash = $null
    stagedCompilerDllHash = $null
    outputs = @()
    error = $null
    cleanupErrors = @()
}
$work = $null
$failure = $null

function Require-Hash([string]$Path, [string]$Expected) {
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected) { throw "Pinned shader input mismatch: $Path" }
    return $actual
}

function Invoke-Compiler([string]$Compiler, [string[]]$Arguments, [string]$Log) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Compiler
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($Compiler)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    $primaryError = $null
    $cleanupError = $null
    $text = $null
    try {
        $started = $process.Start()
        if (-not $started) { throw 'FXC could not start.' }
        $result.compilerInvoked = $true
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) {
            throw 'The owned FXC process exceeded 10 seconds.'
        }
        $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($Log, $text)
        if ($process.ExitCode -ne 0) { throw "FXC exited with code $($process.ExitCode)." }
    } catch {
        $primaryError = $_
    } finally {
        try {
            if ($started -and -not $process.HasExited) {
                $process.Kill($true)
                if (-not $process.WaitForExit(3000)) { throw 'The owned FXC process did not terminate after cleanup.' }
            }
        } catch {
            $cleanupError = $_
            $result.cleanupErrors += [ordered]@{ stage = 'owned-process-stop'; error = $_.Exception.Message }
        } finally {
            try { $process.Dispose() } catch {
                if ($null -eq $cleanupError) { $cleanupError = $_ }
                $result.cleanupErrors += [ordered]@{ stage = 'owned-process-dispose'; error = $_.Exception.Message }
            }
        }
    }
    if ($null -ne $primaryError) { throw $primaryError }
    if ($null -ne $cleanupError) { throw $cleanupError }
    return $text
}

try {
    if (-not $IsWindows) { throw 'The pinned Microsoft FXC requires Windows.' }
    # These exact files were observed in the CI565 Windows SDK inventory.
    # Never pick a newer SDK, download a compiler, or fall back to a system DLL.
    $fxcHash = '3c1d8f3aa0d1c970732f14d61d3a4b5dfa8ca4b7c81d567643f39f6cef412010'
    $dllHash = '8d48f0096f75c46d9c2180ed0fb12c1cb8124b3f3cc6e1262538819cbb23404c'
    $source = Join-Path $PSScriptRoot 'shaders/Saturate180.hlsl'
    $result.sourceHash = Require-Hash $source '2efb90839523e69bf419a8ea7abd08b2f1032040950d0742fd3d3c934ec62d65'
    $root = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
    $sdk = Join-Path $root 'bin/10.0.26100.0/x64'
    $compiler = Join-Path $sdk 'fxc.exe'
    $compilerDll = Join-Path $sdk 'd3dcompiler_47.dll'
    $result.compilerHash = Require-Hash $compiler $fxcHash
    $result.stagedCompilerDllHash = Require-Hash $compilerDll $dllHash
    $work = Join-Path ([IO.Path]::GetTempPath()) ('typelay-sheet-shader-' + [Guid]::NewGuid().ToString('N'))
    $tool = Join-Path $work 'tool'
    New-Item -ItemType Directory -Path $tool | Out-Null
    Copy-Item -LiteralPath $compiler -Destination (Join-Path $tool 'fxc.exe')
    Copy-Item -LiteralPath $compilerDll -Destination (Join-Path $tool 'd3dcompiler_47.dll')
    $compiler = Join-Path $tool 'fxc.exe'
    $compilerDll = Join-Path $tool 'd3dcompiler_47.dll'
    $null = Require-Hash $compiler $fxcHash
    $null = Require-Hash $compilerDll $dllHash
    $bytecodes = @()
    foreach ($name in @('first', 'second')) {
        $directory = Join-Path $work $name
        New-Item -ItemType Directory -Path $directory | Out-Null
        $hlslInput = Join-Path $directory 'Saturate180.hlsl'
        $target = Join-Path $output ($name + '.ps')
        Copy-Item -LiteralPath $source -Destination $hlslInput
        $null = Require-Hash $hlslInput $result.sourceHash
        $arguments = @('/nologo', '/T', 'ps_2_0', '/E', 'main', '/Ges', '/WX', '/O1', '/Fo', $target, $hlslInput)
        $null = Invoke-Compiler $compiler $arguments (Join-Path $output ($name + '-compile.log'))
        $bytes = [IO.File]::ReadAllBytes($target)
        if ($bytes.Length -lt 8 -or [BitConverter]::ToUInt32($bytes, 0) -ne 4294902272
            -or [BitConverter]::ToUInt32($bytes, $bytes.Length - 4) -ne 65535) {
            throw 'Generated output is not a complete ps_2_0 program.'
        }
        $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        $result.outputs += [ordered]@{ name = $name; bytes = $bytes.Length; sha256 = $hash }
        $bytecodes += ,$bytes
    }
    if ([Convert]::ToBase64String($bytecodes[0]) -cne [Convert]::ToBase64String($bytecodes[1])) {
        throw 'Two independent official compilations produced different bytecode.'
    }
    $assembly = Invoke-Compiler $compiler @('/nologo', '/dumpbin', (Join-Path $output 'first.ps')) (Join-Path $output 'assembly.txt')
    if ($assembly -notmatch '(?m)^\s*ps_2_0\s*$') { throw 'Official FXC disassembly did not confirm ps_2_0.' }
    $null = Require-Hash $source $result.sourceHash
    $null = Require-Hash $compiler $fxcHash
    $null = Require-Hash $compilerDll $dllHash
    $result.status = 'generated-twice-identical-not-rendered'
} catch {
    $failure = $_
    $result.status = 'failed'
    $result.error = $_.Exception.Message
} finally {
    try {
        if ($null -ne $work -and (Test-Path -LiteralPath $work)) {
            Remove-Item -LiteralPath $work -Recurse -Force
        }
    } catch {
        $result.cleanupErrors += [ordered]@{ stage = 'owned-temporary-directory'; error = $_.Exception.Message }
        if ($null -eq $failure) { $failure = $_ }
    }
    if ($null -ne $failure) {
        $result.status = 'failed'
        $result.error = $failure.Exception.Message
    }
    try {
        $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
    } catch {
        if ($null -eq $failure) { $failure = $_ }
    }
}
if ($null -ne $failure) { throw $failure }
Write-Output 'Pinned FXC compiled identical ps_2_0 candidates twice. No WPF rendering or product acceptance yet.'
