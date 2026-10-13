# Isolated metadata-only experiment. No downloads, product hooks, or production support changes.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $ChromeArchive,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-fA-F0-9]{64}$')][string] $ExpectedSha256,
    [string] $OutputPath,
    [switch] $StrictCsp,
    [Parameter(DontShow = $true)][switch] $OwnedChild
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT') { throw 'This probe requires an interactive Windows desktop.' }
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$fixture = Join-Path $repo 'tests/windows/fixtures/TypeLay.BrowserIdentityFixture'
# The same fixture-only Job implementation bounds the entire child entry, including restore/extraction.
if (-not $OwnedChild) {
    Add-Type -Path (Join-Path $fixture 'Windows.cs')
    if (-not $OutputPath) {
        $OutputPath = Join-Path $repo ('artifacts/browser-identity-' + [guid]::NewGuid().ToString('N') + '/result.json')
    }
    $OutputPath = [IO.Path]::GetFullPath($OutputPath)
    if ((Test-Path -LiteralPath $OutputPath) -or (Test-Path -LiteralPath ([IO.Path]::ChangeExtension($OutputPath, '.entry.json')))) { throw 'Refusing to replace an existing result.' }
    $childArguments = @('-NoProfile', '-File', $PSCommandPath, '-ChromeArchive',
        (Resolve-Path -LiteralPath $ChromeArchive).Path, '-ExpectedSha256', $ExpectedSha256,
        '-OutputPath', $OutputPath, '-OwnedChild')
    if ($StrictCsp) { $childArguments += '-StrictCsp' }
    $owned = $null
    $entryFailure = ''
    $cleanupError = ''
    $entryCode = 1
    try {
        $shell = (Get-Process -Id $PID).Path
        $owned = [TypeLay.BrowserIdentityFixture.OwnedProcess]::new($shell, [string[]]$childArguments)
        if (-not $owned.WaitForExit(180000)) { $entryFailure = 'entry-timeout'; throw 'entry-timeout' }
        $entryCode = $owned.ExitCode
    } catch { if (-not $entryFailure) { $entryFailure = $_.Exception.GetType().Name } }
    finally {
        if ($null -ne $owned) {
            try { $owned.Dispose() } catch { $cleanupError = $_.Exception.GetType().Name }
        }
    }
    if (-not $entryFailure -and -not (Test-Path -LiteralPath $OutputPath)) { $entryFailure = 'child-result-missing' }
    if ($entryFailure -or $cleanupError) {
        New-Item -ItemType Directory -Path (Split-Path $OutputPath -Parent) -Force | Out-Null
        # Preserve a child's first failure. The watchdog has its own adjacent terminal record.
        $watchdogPath = [IO.Path]::ChangeExtension($OutputPath, '.entry.json')
        [ordered]@{ outcome = 'failed'; failure = $entryFailure; childExitCode = $entryCode;
            processCleanup = -not $cleanupError; cleanupError = $cleanupError;
            resourceCleanup = 'unverified-see-child-result'  } |
            ConvertTo-Json | Set-Content -LiteralPath $watchdogPath -Encoding utf8
        exit 1
    }
    exit $entryCode
}
$pin = Get-Content (Join-Path $fixture 'chrome-for-testing.json') -Raw | ConvertFrom-Json
$archive = (Resolve-Path -LiteralPath $ChromeArchive).Path
$actualSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha256 -ne $ExpectedSha256.ToLowerInvariant()) { throw 'Chrome archive SHA-256 mismatch.' }
$run = Join-Path $repo ('artifacts/browser-identity-' + [guid]::NewGuid().ToString('N'))
if (-not $OutputPath) { $OutputPath = Join-Path $run 'result.json' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) { throw 'Refusing to replace an existing result.' }
New-Item -ItemType Directory -Path $run | Out-Null
$browserDirectory = Join-Path $run 'browser'
$buildDirectory = Join-Path $run 'build'
$code = 1
try {
    Expand-Archive -LiteralPath $archive -DestinationPath $browserDirectory
    $chrome = Join-Path $browserDirectory 'chrome-win64/chrome.exe'
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($chrome)
    if ($version.ProductVersion -ne $pin.version -or $version.ProductName -ne 'Google Chrome for Testing') {
        throw 'Archive is not the pinned Chrome for Testing win64 build.'
    }
    & dotnet build (Join-Path $fixture 'TypeLay.BrowserIdentityFixture.csproj') -c Release -r win-x64 --self-contained false -o $buildDirectory -m:1 -p:TreatWarningsAsErrors=true
    if ($LASTEXITCODE -ne 0) { throw 'Independent fixture build failed.' }
    $arguments = @('--chrome', $chrome, '--output', $OutputPath)
    if ($StrictCsp) { $arguments += '--strict-csp' }
    & (Join-Path $buildDirectory 'TypeLay.BrowserIdentityFixture.exe') @arguments
    $code = $LASTEXITCODE
    if (-not (Test-Path -LiteralPath $OutputPath)) { throw 'Probe did not write its result.' }
    $provenancePath = [IO.Path]::ChangeExtension($OutputPath, '.browser.json')
    if (Test-Path -LiteralPath $provenancePath) { throw 'Refusing to replace existing browser provenance.' }
    [ordered]@{
        version = $pin.version
        revision = $pin.revision
        officialMetadata = $pin.metadataUrl
        officialArchive = $pin.archiveUrl
        archiveSha256 = $actualSha256
    } | ConvertTo-Json | Set-Content -LiteralPath $provenancePath -Encoding utf8
} finally {
    # The fixture terminates its own job before returning. Never kill by process name or PID.
    foreach ($directory in @($buildDirectory, $browserDirectory)) {
        if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
    }
}
exit $code
