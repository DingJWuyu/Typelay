#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if ($env:OS -ne 'Windows_NT') { throw 'Run this release builder on Windows x64.' }
& (Join-Path $PSScriptRoot 'validate.ps1')
$dotnet=(Get-Command dotnet -ErrorAction Stop).Source
$stage=Join-Path $root 'artifacts\release\TypeLay-0.2.0-dev'
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
# Explicit source allowlist prevents publishing local models, user data, or unrelated working files.
foreach ($name in @('src','tests','worker','tools','fixtures','docs','reference','archive','evidence','.github')) {
    $base=Join-Path $root $name
    foreach ($file in Get-ChildItem -LiteralPath $base -File -Recurse) {
        if ($file.FullName -match '[\\/](bin|obj|__pycache__|\.git)[\\/]' -or $file.Extension -eq '.pyc') { continue }
        $dest=Join-Path $stage $file.FullName.Substring($root.Length+1)
        New-Item -ItemType Directory -Force (Split-Path $dest -Parent) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $dest
    }
}
foreach ($name in @('TypeLay.exe','Start-TypeLay.cmd','README.md','开始使用.txt','VERIFICATION_STATUS.md','THIRD_PARTY_NOTICES.md','TypeLay.slnx','Directory.Build.props','NuGet.config','global.json','.gitignore')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $stage $name)
}
Push-Location $root
try {
    $app=Join-Path $stage 'app'
    foreach ($project in @('TypeLay.App','TypeLay.Broker')) {
        & $dotnet publish (Join-Path 'src' $project) -c Release -r win-x64 --self-contained true -o $app -p:PublishSingleFile=false -p:PublishTrimmed=false -p:TargetPlatformDisplayName=Windows
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
    }
    if (-not (Test-Path (Join-Path $app 'TypeLay.App.exe')) -or -not (Test-Path (Join-Path $app 'TypeLay.Broker.exe'))) { throw 'Missing application host.' }
    @{schema=1;build='passed';logicTests='passed';distribution='prebuilt-self-contained';runtimeInference='not-run';windowsInteraction='not-run';safeWriteback='blocked';at=[DateTime]::UtcNow.ToString('o')} |
        ConvertTo-Json | Set-Content (Join-Path $stage 'evidence\windows-publish.json') -Encoding UTF8
    $manifest=Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+$_.FullName.Substring($stage.Length+1).Replace('\','/')
    }
    $manifest | Set-Content (Join-Path $stage 'MANIFEST.sha256') -Encoding UTF8
    $zip=Join-Path $root 'artifacts\TypeLay-Windows-0.2.0-dev-prebuilt.zip'
    Compress-Archive -LiteralPath $stage -DestinationPath $zip -Force
    Get-FileHash -LiteralPath $zip -Algorithm SHA256 | Format-List
    Write-Host "Created $zip . Models still require first-use installation. Safety release blocker remains open."
} finally { Pop-Location }
