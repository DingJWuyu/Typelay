#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$homeDirectory = Split-Path $PSScriptRoot -Parent
$application = Join-Path $homeDirectory 'app/TypeLay.App.exe'
try {
    if (-not (Test-Path -LiteralPath $application -PathType Leaf)) {
        throw 'Incomplete distribution.'
    }
    $env:TYPELAY_HOME = $homeDirectory
    Start-Process -FilePath $application -WorkingDirectory (Split-Path $application -Parent) -ErrorAction Stop
} catch {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(
        'TypeLay 无法启动。请将完整 Windows 预览包解压到同一个文件夹后重试，详见 README.md。',
        'TypeLay', 'OK', 'Error') | Out-Null
    exit 1
}
