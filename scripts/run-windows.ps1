param(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not $IsWindows) { throw '请在 Windows 11 的 PowerShell 7 中执行此脚本。' }
dotnet --version
$project = Join-Path $root 'src/VoicePilot.App/VoicePilot.App.csproj'
dotnet build $project --configuration $Configuration -p:Platform=$Architecture
$exe = Join-Path $root "src/VoicePilot.App/bin/$Architecture/$Configuration/net10.0-windows10.0.19041.0/win-$Architecture/VoicePilot.exe"
if (-not (Test-Path $exe)) { $exe = Join-Path $root "src/VoicePilot.App/bin/$Configuration/net10.0-windows10.0.19041.0/win-$Architecture/VoicePilot.exe" }
if (-not (Test-Path $exe)) { throw "未找到应用输出：$exe" }
Write-Host "启动 $exe。请先安装 Windows App SDK 2.7.0 Runtime。" -ForegroundColor Green
& $exe
