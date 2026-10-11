#requires -Version 5.1
[CmdletBinding()]
param([switch]$ConsoleDiagnostics)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$data = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TypeLay'
$env:TYPELAY_HOME = $root
$script:window = $null
$script:message = $null
$script:bar = $null
$script:cancelled = $false
$script:phase = 'START'
$lock = $null
$locked = $false
$script:log = $null

function Pump-UI {
    if ($null -ne $script:window) { [Windows.Forms.Application]::DoEvents() }
    if ($script:cancelled) { throw [OperationCanceledException]::new('Cancelled by user.') }
}
function Status([string]$phase, [string]$text) {
    $script:phase = $phase
    if ($null -ne $script:message) { $script:message.Text = $text }
    if ($ConsoleDiagnostics) { Write-Host "[$phase] $text" }
    Pump-UI
}
function Show-Setup {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [Windows.Forms.Application]::EnableVisualStyles()
    $script:window = New-Object Windows.Forms.Form
    $script:window.Text = 'TypeLay · 首次启动'
    $script:window.ClientSize = New-Object Drawing.Size(550,275)
    $script:window.StartPosition = 'CenterScreen'
    $script:window.FormBorderStyle = 'FixedDialog'
    $script:window.MaximizeBox = $false
    $script:window.Font = New-Object Drawing.Font('Segoe UI',10)
    $title = New-Object Windows.Forms.Label
    $title.Text = '准备 TypeLay 开发版'
    $title.Font = New-Object Drawing.Font('Segoe UI',16,[Drawing.FontStyle]::Bold)
    $title.SetBounds(26,22,500,32)
    $script:window.Controls.Add($title)
    $intro = New-Object Windows.Forms.Label
    $intro.Text = '此包包含原生启动器和完整源码。首次启动下载私有 .NET SDK，自动构建并运行逻辑回归；后续直接启动已构建客户端。接下来可在设置中下载本地模型。'
    $intro.SetBounds(28,67,490,68)
    $script:window.Controls.Add($intro)
    $script:message = New-Object Windows.Forms.Label
    $script:message.SetBounds(28,143,490,39)
    $script:window.Controls.Add($script:message)
    $script:bar = New-Object Windows.Forms.ProgressBar
    $script:bar.Style = 'Marquee'; $script:bar.SetBounds(28,190,490,6)
    $script:window.Controls.Add($script:bar)
    $note = New-Object Windows.Forms.Label
    $note.Text = '无需管理员权限，不修改全局 Python / .NET。关闭窗口可取消。'
    $note.ForeColor = [Drawing.Color]::DimGray
    $note.SetBounds(28,217,490,35)
    $script:window.Controls.Add($note)
    $script:window.Add_FormClosing({ $script:cancelled = $true })
    $script:window.Show(); Pump-UI
}
function Download([string]$url, [string]$destination) {
    $uri = [Uri]$url
    $allowed = @('builds.dotnet.microsoft.com','dotnetcli.blob.core.windows.net','dotnetcli.azureedge.net','download.visualstudio.microsoft.com')
    if ($uri.Scheme -ne 'https' -or $uri.DnsSafeHost -notin $allowed) { throw 'SDK download host was not allowed.' }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    # The vendor's release metadata supplies the SHA-512 for the final SDK bytes.
    # HttpClient's redirect policy never permits an HTTPS -> HTTP redirect.
    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $client = New-Object Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(20)
    $response = $null
    $stream = $null
    $output = $null
    $part = $destination + '.part'
    try {
        for ($hop=0; $hop -lt 6; $hop++) {
            if ($uri.Scheme -ne 'https' -or $uri.DnsSafeHost -notin $allowed) { throw 'SDK redirect host was not allowed.' }
            $pending = $client.GetAsync($uri,[Net.Http.HttpCompletionOption]::ResponseHeadersRead)
            while (-not $pending.IsCompleted) { Start-Sleep -Milliseconds 40; Pump-UI }
            $response = $pending.GetAwaiter().GetResult()
            if ([int]$response.StatusCode -ge 300 -and [int]$response.StatusCode -lt 400) {
                if ($null -eq $response.Headers.Location) { throw 'SDK redirect omitted Location.' }
                $uri = [Uri]::new($uri,$response.Headers.Location)
                $response.Dispose(); $response=$null
                continue
            }
            $response.EnsureSuccessStatusCode() | Out-Null
            break
        }
        if ($null -eq $response) { throw 'Too many SDK redirects.' }
        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $output = [IO.File]::Open($part,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
        $buffer = New-Object byte[] 131072
        [long]$total = 0
        while ($true) {
            $read = $stream.ReadAsync($buffer,0,$buffer.Length)
            while (-not $read.IsCompleted) { Start-Sleep -Milliseconds 25; Pump-UI }
            $count = $read.GetAwaiter().GetResult()
            if ($count -eq 0) { break }
            $total += $count
            if ($total -gt 1200000000) { throw 'SDK download exceeded the size limit.' }
            $output.Write($buffer,0,$count); Pump-UI
            if ($script:phase -eq 'SDK-DOWNLOAD') { $script:message.Text = ('正在下载私有 SDK：已接收 {0:N0} MB' -f ($total/1MB)) }
        }
        $output.Flush($true); $output.Dispose(); $output=$null
        Move-Item -LiteralPath $part -Destination $destination -Force
    } finally {
        if ($null -ne $output) { $output.Dispose() }
        if ($null -ne $stream) { $stream.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
        $client.Dispose(); $handler.Dispose()
    }
}
function Installed-Sdk {
    foreach ($directory in @('runtime\dotnet','dotnet')) {
        $candidate = Join-Path $data $directory
        $executable = Join-Path $candidate 'dotnet.exe'
        if ((Test-Path -LiteralPath $executable) -and
            (Test-Path -LiteralPath (Join-Path $candidate 'sdk\10.0.401\dotnet.dll'))) { return $executable }
    }
    return $null
}
function Sdk-Path {
    $sdkRoot = Join-Path $data 'runtime\dotnet'
    $sdkExe = Join-Path $sdkRoot 'dotnet.exe'
    $pin = '10.0.401'
    $installed = Installed-Sdk
    if ($null -ne $installed) { return $installed }
    Status 'SDK-METADATA' '获取微软官方 SDK 版本与校验信息…'
    $cache = Join-Path $data 'downloads'; New-Item -ItemType Directory -Force $cache | Out-Null
    $metadataFile = Join-Path $cache 'dotnet-10.0-releases.json'
    Download 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json' $metadataFile
    $metadata = Get-Content -LiteralPath $metadataFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $packages = @()
    foreach ($release in $metadata.releases) {
        if ($release.PSObject.Properties.Name -contains 'sdk') { $packages += $release.sdk }
        if ($release.PSObject.Properties.Name -contains 'sdks') { $packages += $release.sdks }
    }
    $sdk = $packages | Where-Object { $_.version -eq $pin } | Select-Object -First 1
    if ($null -eq $sdk) { throw "The official manifest did not contain SDK $pin." }
    $asset = $sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.url.EndsWith('.zip') } | Select-Object -First 1
    if ($null -eq $asset -or $asset.hash -notmatch '^[0-9a-fA-F]{128}$') { throw 'SDK integrity metadata was missing.' }
    $archive = Join-Path $cache "dotnet-sdk-$pin-win-x64.zip"
    if (-not (Test-Path $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $asset.hash) {
        Status 'SDK-DOWNLOAD' '正在下载私有 SDK…'; Download $asset.url $archive
    }
    Status 'SDK-VERIFY' '校验 SDK 并解压到当前用户目录…'
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $asset.hash) {
        Remove-Item -LiteralPath $archive -Force; throw 'SDK SHA-512 verification failed.'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stage = Join-Path $data 'runtime\dotnet.staging'
    if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Force $stage | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        [long]$expanded = 0
        $prefix = [IO.Path]::GetFullPath($stage) + [IO.Path]::DirectorySeparatorChar
        foreach ($entry in $zip.Entries) {
            $expanded += $entry.Length
            $path = [IO.Path]::GetFullPath((Join-Path $stage $entry.FullName))
            if (-not $path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or $entry.FullName.Contains(':') -or $expanded -gt 4000000000) { throw 'SDK archive path or size was invalid.' }
        }
    } finally { $zip.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($archive,$stage)
    Pump-UI
    if (Test-Path $sdkRoot) { Remove-Item -LiteralPath $sdkRoot -Recurse -Force }
    Move-Item -LiteralPath $stage -Destination $sdkRoot
    return $sdkExe
}
function Run-DotNet([string[]]$arguments, [string]$work, [string]$label) {
    Status 'BUILD' $label
    # Every argument originates from this package, never from original input text.
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $env:TYPELAY_DOTNET
    $info.Arguments = (($arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }) -join ' ')
    $info.WorkingDirectory=$work; $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    $p = New-Object Diagnostics.Process; $p.StartInfo=$info; $started=$false
    try {
        $started=$p.Start(); if (-not $started) { throw 'Build process could not start.' }
        $stdout = $p.StandardOutput.ReadToEndAsync(); $stderr=$p.StandardError.ReadToEndAsync()
        while (-not $p.HasExited) { Start-Sleep -Milliseconds 60; Pump-UI }
        $p.WaitForExit()
        Add-Content -LiteralPath $script:log -Value ("`r`n--- " + $label + " ---`r`n" + $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()) -Encoding UTF8
        if ($p.ExitCode -ne 0) { throw "Build or test failed: $label (exit $($p.ExitCode))." }
    } finally {
        if ($started -and -not $p.HasExited) {
            # Kill the complete build-process tree; only this child PID is targeted.
            & "$env:SystemRoot\System32\taskkill.exe" /PID $p.Id /T /F 2>$null | Out-Null
        }
        $p.Dispose()
    }
}
function Launch([string]$client, [bool]$hosted) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    if ($hosted) { $info.FileName=$client }
    else { $info.FileName=$env:TYPELAY_DOTNET; $info.Arguments='"'+$client+'"' }
    $info.WorkingDirectory=$root
    $process = [Diagnostics.Process]::Start($info)
    if ($null -eq $process) { throw 'Client process could not start.' }
    $process.Dispose()
}
try {
    if (-not [Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { throw 'This development package requires Windows x64.' }
    New-Item -ItemType Directory -Force $data | Out-Null
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $lock = New-Object Threading.Mutex($false,('Local\TypeLay.Setup.'+$sid))
    try { $locked = $lock.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked=$true }
    if (-not $locked) {
        Add-Type -AssemblyName System.Windows.Forms
        [Windows.Forms.MessageBox]::Show('TypeLay 的另一个启动流程正在运行。请查看已打开的准备窗口。','TypeLay') | Out-Null
        exit 0
    }
    $prebuilt = Join-Path $root 'app\TypeLay.App.exe'
    if (Test-Path $prebuilt) { Launch $prebuilt $true; exit 0 }
    $inputs = @(Get-ChildItem -LiteralPath (Join-Path $root 'src'),(Join-Path $root 'tests'),(Join-Path $root 'fixtures') -Recurse -File |
        Where-Object { $_.Extension -in @('.cs','.csproj','.manifest') -and $_.FullName -notmatch '[\\/](obj|bin)[\\/]' })
    $inputs += Get-Item (Join-Path $root 'Directory.Build.props'),(Join-Path $root 'TypeLay.slnx'),(Join-Path $root 'global.json'),(Join-Path $root 'NuGet.config')
    $hashInput = (($inputs | Sort-Object FullName | ForEach-Object { $_.FullName.Substring($root.Length).Replace('\','/') + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join "`n")
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $fingerprint=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($hashInput)))).Replace('-','').Substring(0,24) } finally { $sha.Dispose() }
    $build = Join-Path $data ('build\'+$fingerprint)
    $client = Join-Path $build 'app\TypeLay.App.exe'
    $installedSdk = Installed-Sdk
    $env:TYPELAY_DOTNET = if ($null -ne $installedSdk) { $installedSdk } else { Join-Path $data 'runtime\dotnet\dotnet.exe' }
    $env:DOTNET_ROOT = Split-Path $env:TYPELAY_DOTNET -Parent
    if ((Test-Path (Join-Path $build 'build-complete.json')) -and (Test-Path $client) -and (Test-Path $env:TYPELAY_DOTNET)) { Launch $client $true; exit 0 }
    Show-Setup
    $logs=Join-Path $data 'build-logs'; New-Item -ItemType Directory -Force $logs | Out-Null
    $script:log=Join-Path $logs ('build-'+$fingerprint+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.log')
    'TypeLay bootstrap: compiler and deterministic logic tests only; no real user input is present.' | Set-Content -LiteralPath $script:log -Encoding UTF8
    $env:TYPELAY_DOTNET=Sdk-Path
    $env:DOTNET_ROOT=Split-Path $env:TYPELAY_DOTNET -Parent
    Status 'COPY' '准备隔离的构建目录…'
    $source = Join-Path $build 'source'; New-Item -ItemType Directory -Force $source | Out-Null
    foreach ($file in $inputs) {
        $target=Join-Path $source $file.FullName.Substring($root.Length+1)
        New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
    Run-DotNet @('build','TypeLay.slnx','-c','Release','--nologo','-p:UseSharedCompilation=false','-p:TargetPlatformDisplayName=Windows') $source '正在构建 Windows 客户端与输入服务…'
    Run-DotNet @('run','--project','tests\TypeLay.Core.Tests','-c','Release','--no-build') $source '运行状态机与安全判定回归…'
    Run-DotNet @('run','--project','tests\TypeLay.Integration.Tests','-c','Release','--no-build') $source '运行协议、选区与敏感字段规则回归…'
    $publish=Join-Path $build 'app'
    Run-DotNet @('publish','src\TypeLay.App','-c','Release','--no-restore','-o',$publish,'-p:TargetPlatformDisplayName=Windows') $source '整理客户端运行文件…'
    Run-DotNet @('publish','src\TypeLay.Broker','-c','Release','--no-restore','-o',$publish,'-p:TargetPlatformDisplayName=Windows') $source '整理输入服务运行文件…'
    # The marker means compilation + deterministic tests, never Windows GUI / IME acceptance.
    @{ schema=1; sourceFingerprint=$fingerprint; sdk='10.0.401'; build='passed'; logicTests='passed'; windowsGui='not-run'; physicalTab='not-run'; ime='not-run'; at=[DateTime]::UtcNow.ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $build 'build-complete.json') -Encoding UTF8
    Status 'LAUNCH' '客户端已构建。正在打开 TypeLay…'
    Launch $client $true
} catch {
    if ($_.Exception -isnot [OperationCanceledException]) {
        Add-Type -AssemblyName System.Windows.Forms
        $detail = '启动未完成。阶段：'+$script:phase+"`r`n"+$_.Exception.Message
        if ($null -ne $script:log) {
            $detail += "`r`n`r`n构建记录："+$script:log
            Add-Content -LiteralPath $script:log -Value ("BOOTSTRAP-ERROR "+$script:phase+"`r`n"+$_.Exception.Message) -Encoding UTF8
        }
        $detail += "`r`n`r`n不会启动未构建完成的客户端。修复网络或错误后，可再次双击 TypeLay.exe 重试。"
        if ($ConsoleDiagnostics) { Write-Host $detail }
        else { [Windows.Forms.MessageBox]::Show($detail,'TypeLay 启动失败',[Windows.Forms.MessageBoxButtons]::OK,[Windows.Forms.MessageBoxIcon]::Error) | Out-Null }
    }
    exit 1
} finally {
    if ($null -ne $script:window) { $script:window.Dispose() }
    if ($locked) { $lock.ReleaseMutex() }
    if ($null -ne $lock) { $lock.Dispose() }
}
