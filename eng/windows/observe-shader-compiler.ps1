#requires -Version 7.2
# Research inventory only. Never downloads, invokes FXC, or gates product support.
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OutputPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$result = [ordered]@{
    kind = 'shader-compiler-inventory'
    sourceSha = $env:GITHUB_SHA
    imageOS = $env:ImageOS
    imageVersion = $env:ImageVersion
    status = 'not-observed'
    found = $false
    compilerInvoked = $false
    productAcceptance = $false
    registeredRoots = @()
    compilers = @()
    systemCompilerDll = $null
    errors = @()
}

function Describe-File([string]$Path) {
    $description = [ordered]@{ path = $Path; status = 'missing'; fileVersion = $null; productVersion = $null; sha256 = $null; companyName = $null; originalFilename = $null; error = $null }
    try {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            $file = Get-Item -LiteralPath $Path
            $description.fileVersion = $file.VersionInfo.FileVersion
            $description.productVersion = $file.VersionInfo.ProductVersion
            $description.sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
            $description.companyName = $file.VersionInfo.CompanyName
            $description.originalFilename = $file.VersionInfo.OriginalFilename
            $description.status = 'present'
        }
    } catch {
        $description.status = 'unreadable'
        $description.error = $_.Exception.GetType().Name
    }
    return $description
}

try {
    if (-not $IsWindows) {
        $result.status = 'not-windows'
    } else {
        foreach ($key in @('HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots')) {
            try {
                if (Test-Path -LiteralPath $key) {
                    $entry = Get-ItemProperty -LiteralPath $key
                    $property = $entry.PSObject.Properties['KitsRoot10']
                    if ($null -ne $property -and -not [string]::IsNullOrEmpty($property.Value)) {
                        $result.registeredRoots += [string]$property.Value
                    }
                }
            } catch {
                $result.errors += [ordered]@{ stage = 'registered-root'; path = $key; error = $_.Exception.GetType().Name }
            }
        }
        $result.registeredRoots = @($result.registeredRoots | Sort-Object -Unique)
        foreach ($root in $result.registeredRoots) {
            $bin = Join-Path $root 'bin'
            try {
                if (-not (Test-Path -LiteralPath $bin -PathType Container)) { continue }
                foreach ($version in (Get-ChildItem -LiteralPath $bin -Directory | Sort-Object Name)) {
                    $fxc = Describe-File (Join-Path $version.FullName 'x64/fxc.exe')
                    if ($fxc.status -eq 'missing') { continue }
                    $result.compilers += [ordered]@{
                        sdkDirectory = $version.Name
                        compiler = $fxc
                        adjacentCompilerDll = Describe-File (Join-Path $version.FullName 'x64/d3dcompiler_47.dll')
                    }
                }
            } catch {
                $result.errors += [ordered]@{ stage = 'sdk-bin'; path = $bin; error = $_.Exception.GetType().Name }
            }
        }
        $result.systemCompilerDll = Describe-File (Join-Path $env:SystemRoot 'System32/d3dcompiler_47.dll')
        $result.found = @($result.compilers | Where-Object { $_.compiler.status -eq 'present' }).Count -gt 0
        $unreadable = @($result.compilers | Where-Object { $_.compiler.status -eq 'unreadable' -or $_.adjacentCompilerDll.status -eq 'unreadable' }).Count -gt 0
        $incomplete = $unreadable -or $result.errors.Count -gt 0 -or $result.systemCompilerDll.status -eq 'unreadable'
        $result.status = if ($incomplete) { 'incomplete' } elseif ($result.found) { 'found-not-invoked' } else { 'missing' }
    }
} catch {
    $result.status = 'observation-failed'
    $result.errors += [ordered]@{ stage = 'inventory'; error = $_.Exception.GetType().Name }
}

try {
    $destination = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    $result | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $destination -Encoding utf8
} catch {
    # The workflow step is also non-blocking, including output/runner failures.
    Write-Warning ('Shader inventory output failed: ' + $_.Exception.GetType().Name)
}
Write-Output "Shader inventory: $($result.status); found=$($result.found); compilerInvoked=False. Not product acceptance."
