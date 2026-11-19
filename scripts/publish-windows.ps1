param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not $IsWindows) { throw '请在 Windows 构建机上执行此发布脚本。' }
if ($Version -notmatch '^v(\d+\.\d+\.\d+)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw "版本必须为 vMAJOR.MINOR.PATCH，可带 SemVer 预发布后缀：$Version"
}

$packageVersion = $Version.Substring(1)
$assemblyVersion = "$($Matches[1]).0"
$commit = if ($env:GITHUB_SHA) { $env:GITHUB_SHA } else { (git rev-parse HEAD).Trim() }
$informationalVersion = "$packageVersion+commit.$commit"
$publishDirectory = Join-Path $root "artifacts/publish/$Version/win-$Architecture"
$releaseDirectory = Join-Path $root 'artifacts/release'
$zipName = "VoicePilot-$Version-win-$Architecture.zip"
$zipPath = Join-Path $releaseDirectory $zipName

New-Item -ItemType Directory -Force -Path $publishDirectory, $releaseDirectory | Out-Null
$project = Join-Path $root 'src/VoicePilot.App/VoicePilot.App.csproj'
& dotnet publish $project `
    --configuration Release `
    --runtime "win-$Architecture" `
    --self-contained true `
    --output $publishDirectory `
    --nologo `
    -p:Platform=$Architecture `
    -p:Version=$packageVersion `
    -p:AssemblyVersion=$assemblyVersion `
    -p:FileVersion=$assemblyVersion `
    -p:InformationalVersion=$informationalVersion `
    -p:WindowsAppSDKSelfContained=true `
    -p:WindowsPackageType=None `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

$exe = Join-Path $publishDirectory 'VoicePilot.exe'
$runtimeConfig = Join-Path $publishDirectory 'VoicePilot.runtimeconfig.json'
$windowsAppRuntime = Join-Path $publishDirectory 'Microsoft.WindowsAppRuntime.dll'
foreach ($requiredFile in @($exe, $runtimeConfig, $windowsAppRuntime)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "发布包缺少必需文件：$requiredFile"
    }
}

$actualVersion = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
if ($actualVersion -ne $assemblyVersion) {
    throw "EXE 文件版本不匹配：期望 $assemblyVersion，实际 $actualVersion"
}

$buildInfo = [ordered]@{
    Product = 'VoicePilot'
    Version = $Version
    Architecture = $Architecture
    Commit = $commit
    BuiltAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    DotNetRuntime = 'self-contained'
    WindowsAppSDK = '2.7.0 self-contained'
    CodeSigned = $false
    LaunchFile = 'VoicePilot.exe'
}
$buildInfo | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publishDirectory 'PACKAGE-INFO.json') -Encoding utf8

Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash *$zipName" | Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt') -Encoding ascii

Write-Host "发布包：$zipPath"
Write-Host "SHA-256：$hash"
Write-Host 'ZIP 内 VoicePilot.exe 不能脱离同目录的 DLL 单独运行；此包未进行代码签名。'
