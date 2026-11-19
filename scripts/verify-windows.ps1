$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not $IsWindows) { throw '请在 Windows 11 PowerShell 7 中执行。' }

dotnet test tests/VoicePilot.Tests/VoicePilot.Tests.csproj --configuration Release --nologo
dotnet build src/VoicePilot.App/VoicePilot.App.csproj --configuration Release -p:Platform=x64 --nologo
Write-Host '核心测试和 WinUI x64 构建已完成。请继续执行 docs/windows-acceptance.md；构建成功不代表音频设备验收通过。' -ForegroundColor Green
