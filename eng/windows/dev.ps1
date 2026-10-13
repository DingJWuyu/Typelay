#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('help', 'restore', 'build', 'test', 'check', 'format', 'pack', 'run', 'e2e', 'native-e2e')]
    [string]$Task = 'help',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $PSScriptRoot '../shared/commands.ps1')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:PYTHONDONTWRITEBYTECODE = '1'
$oldPythonPath = $env:PYTHONPATH
$env:PYTHONPATH = Join-Path $root 'src/shared/TypeLay.Worker'
$artifacts = Join-Path $root 'artifacts'
$archive = Join-Path $artifacts 'TypeLay-Windows-preview.zip'

function Restore-Workspace {
    Invoke-Checked dotnet @('restore', 'TypeLay.slnx', '--locked-mode')
}
function Build-Workspace {
    Restore-Workspace
    Invoke-Checked dotnet @('build', 'TypeLay.slnx', '-c', $Configuration, '--no-restore', '-warnaserror')
}
function Test-Workspace {
    Build-Workspace
    Invoke-Checked dotnet @('test', 'TypeLay.slnx', '-c', $Configuration, '--no-build', '--no-restore',
        '--logger', 'trx', '--results-directory', (Join-Path $artifacts 'test-results'))
    Invoke-Checked python @('-m', 'unittest', 'discover', '-s', 'tests/shared/worker', '-v')
}
function Publish-Distribution([bool]$SelfContained) {
    if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitProcess) {
        throw 'Publishing requires Windows x64. Use shared-check for portable behavior and protocol tests.'
    }
    $base = if ($SelfContained) { 'package' } else { 'dev' }
    $stage = Join-Path $artifacts "$base/TypeLay"
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Force $stage | Out-Null
    foreach ($project in @('TypeLay.App', 'TypeLay.Broker', 'TypeLay.Practice')) {
        Invoke-Checked dotnet @('publish', "src/windows/$project/$project.csproj", '-c', $Configuration,
            '-r', 'win-x64', '--self-contained', $SelfContained.ToString().ToLowerInvariant(),
            '-o', (Join-Path $stage 'app'), '-p:PublishSingleFile=false', '-p:PublishTrimmed=false')
    }
    if ($SelfContained) {
        # Resolve actual packs; preserve every upstream license/notice without inventing absent files.
        $metadata = & dotnet msbuild 'src/windows/TypeLay.App/TypeLay.App.csproj' -nologo -verbosity:quiet `
            -target:ResolveFrameworkReferences -getItem:ResolvedRuntimePack `
            "-property:Configuration=$Configuration" -property:RuntimeIdentifier=win-x64 -property:SelfContained=true
        if ($LASTEXITCODE -ne 0) { throw 'Could not resolve runtime-pack notices.' }
        $packs = @(($metadata -join "`n" | ConvertFrom-Json).Items.ResolvedRuntimePack)
        if ($packs.Count -eq 0) { throw 'Self-contained publishing resolved no runtime packs.' }
        foreach ($pack in $packs) {
            $notices = @(Get-ChildItem -LiteralPath $pack.PackageDirectory -File -Recurse |
                Where-Object { $_.Name -match '(^|[._-])(LICENSE|LICENCE|COPYING|NOTICE|THIRD[-_]?PARTY)' })
            if (-not @($notices | Where-Object Name -Match '^LICENSE').Count) {
                throw "Runtime pack $($pack.NuGetPackageId) has no upstream license."
            }
            $destination = Join-Path $stage "app/licenses/$($pack.NuGetPackageId.ToLowerInvariant())/$($pack.NuGetPackageVersion)"
            foreach ($notice in $notices) {
                $target = Join-Path $destination ([IO.Path]::GetRelativePath($pack.PackageDirectory, $notice.FullName))
                New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
                Copy-Item -LiteralPath $notice.FullName -Destination $target
            }
        }
    }
    # The deployment surface is explicit. Never copy a repository, developer data, or model cache.
    New-Item -ItemType Directory -Force (Join-Path $stage 'worker/typelay_worker') | Out-Null
    Copy-Item 'src/shared/TypeLay.Worker/run_worker.py', 'src/shared/TypeLay.Worker/requirements.txt' (Join-Path $stage 'worker')
    Copy-Item 'src/shared/TypeLay.Worker/typelay_worker/*.py' (Join-Path $stage 'worker/typelay_worker')
    New-Item -ItemType Directory -Force (Join-Path $stage 'bootstrap') | Out-Null
    Copy-Item 'packaging/windows/start.ps1' (Join-Path $stage 'bootstrap/start.ps1')
    Copy-Item 'packaging/windows/Start-TypeLay.cmd' $stage
    Copy-Item 'docs/platforms/windows/README.md' (Join-Path $stage 'README.md')
    Copy-Item 'THIRD_PARTY_NOTICES.md' $stage
    Invoke-Checked python @('eng/windows/build-launcher.py', '--output', (Join-Path $stage 'TypeLay.exe'))
    if ($SelfContained) {
        Compress-Archive -LiteralPath $stage -DestinationPath $archive -Force
        $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content "$archive.sha256" -Encoding utf8
        Test-Distribution
    } else {
        Start-Process -FilePath (Join-Path $stage 'TypeLay.exe') -WorkingDirectory $stage
    }
}
function Test-Distribution {
    if (-not (Test-Path -LiteralPath $archive)) { throw 'Run ./dev.ps1 pack first.' }
    $previous = $env:TYPELAY_PACKAGE_ZIP
    try {
        $env:TYPELAY_PACKAGE_ZIP = $archive
        Invoke-Checked python @('-m', 'unittest', 'discover', '-s', 'tests/windows', '-p', 'test_*.py', '-v')
    } finally { $env:TYPELAY_PACKAGE_ZIP = $previous }
}
Push-Location $root
try {
    switch ($Task) {
        help {
            Write-Output 'Usage: ./dev.ps1 <restore|build|test|check|shared-check|format|pack|run|e2e|native-e2e> [-Configuration Debug|Release]'
            Write-Output 'check: build, behavior/contract tests, Python tests, compiler/analyzer checks and formatting.'
            Write-Output 'shared-check: portable Core/Runtime/Protocol and worker tests; no Windows desktop or model installation.'
            Write-Output 'pack: self-contained Windows distribution, checksum and relocated-package/process E2E.'
            Write-Output 'native-e2e: controlled Win32 fixture adapter contracts; requires an unlocked Windows desktop.'
        }
        restore { Restore-Workspace }
        build { Build-Workspace }
        test { Test-Workspace }
        check {
            Test-Workspace
            Invoke-Checked dotnet @('format', 'TypeLay.slnx', '--verify-no-changes', '--no-restore')
            Invoke-Checked python @('-m', 'ruff', 'check', '.')
            Invoke-Checked python @('-m', 'ruff', 'format', '--check', '--diff', '.')
        }
        format {
            Restore-Workspace
            Invoke-Checked dotnet @('format', 'TypeLay.slnx', '--no-restore')
            Invoke-Checked python @('-m', 'ruff', 'format', '.')
            Invoke-Checked python @('-m', 'ruff', 'check', '--fix', '.')
            Invoke-Checked python @('-m', 'ruff', 'format', '.')
        }
        pack { Publish-Distribution $true }
        run { Publish-Distribution $false }
        e2e { Test-Distribution }
        native-e2e {
            Build-Workspace
            $fixture = Join-Path $root "tests/windows/fixtures/TypeLay.NativeFixture/bin/$Configuration/net10.0/TypeLay.NativeFixture.dll"
            Invoke-Checked dotnet @('run', '--project', 'tests/windows/TypeLay.Native.Tests', '-c', $Configuration,
                '--no-build', '--', $fixture)
        }
    }
} finally {
    Pop-Location
    $env:PYTHONPATH = $oldPythonPath
}
