param(
    [ValidateRange(1, 168)]
    [int]$Hours = 1,
    [string]$ResultsDirectory = 'artifacts/performance',
    [switch]$RequireAudioDevices
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not $IsWindows) { throw '性能采集必须在目标 Windows 11 机器上运行。' }
$exe = Join-Path $root 'src/VoicePilot.App/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/VoicePilot.exe'
if (-not (Test-Path $exe)) { throw "先运行 scripts/run-windows.ps1 -Configuration Release 构建应用。" }

New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
$resultsPath = Join-Path $ResultsDirectory ("voicepilot-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.csv')
$metadata = [ordered]@{
    StartedAt = (Get-Date).ToString('o')
    HoursRequested = $Hours
    Computer = $env:COMPUTERNAME
    Windows = (Get-CimInstance Win32_OperatingSystem).Caption
    WindowsVersion = (Get-CimInstance Win32_OperatingSystem).Version
    CPU = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
    LogicalProcessors = $env:NUMBER_OF_PROCESSORS
    TotalMemoryGB = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 2)
    DotNet = (dotnet --version)
    ServiceProvider = '以 UI 中实际选项为准；模拟结果不是端到端服务性能'
    Notes = '请在 UI 选择固定非默认麦克风和播放设备，手动开始并持续播放测试语音。本脚本不自动开启录音。'
}
$metadata | ConvertTo-Json | Set-Content -Path ([IO.Path]::ChangeExtension($resultsPath, '.json')) -Encoding UTF8
$process = Start-Process -FilePath $exe -PassThru
"Timestamp,ElapsedSeconds,CPUSeconds,CPUAveragePercent,WorkingSetMB,PrivateMemoryMB,ThreadCount,HandleCount,Responding" |
    Set-Content -Path $resultsPath -Encoding UTF8
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$previousCPU = 0.0
$previousTime = $stopwatch.Elapsed
try {
    while (-not $process.HasExited -and $stopwatch.Elapsed -lt [TimeSpan]::FromHours($Hours)) {
        $process.Refresh()
        $now = $stopwatch.Elapsed
        $cpuSeconds = $process.TotalProcessorTime.TotalSeconds
        $cpuPercent = 100 * ($cpuSeconds - $previousCPU) / ($now - $previousTime).TotalSeconds / [Environment]::ProcessorCount
        [pscustomobject]@{
            Timestamp = (Get-Date).ToString('o')
            ElapsedSeconds = [math]::Round($now.TotalSeconds, 1)
            CPUSeconds = [math]::Round($cpuSeconds, 3)
            CPUAveragePercent = [math]::Round($cpuPercent, 4)
            WorkingSetMB = [math]::Round($process.WorkingSet64 / 1MB, 2)
            PrivateMemoryMB = [math]::Round($process.PrivateMemorySize64 / 1MB, 2)
            ThreadCount = $process.Threads.Count
            HandleCount = $process.HandleCount
            Responding = $process.Responding
        } | ConvertTo-Csv -NoTypeInformation | Select-Object -Skip 1 | Add-Content -Path $resultsPath -Encoding UTF8
        $previousCPU = $cpuSeconds
        $previousTime = $now
        Start-Sleep -Seconds 5
    }
}
finally {
    if (-not $process.HasExited) {
        Write-Warning '测试时长已到；请在 UI 点击停止采集后关闭应用，脚本不会自动录音或上传音频。'
    }
}
Write-Host "性能原始采样已保存：$resultsPath。请结合 docs/windows-acceptance.md 填写结论。" -ForegroundColor Green
