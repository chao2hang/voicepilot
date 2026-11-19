# 自动化测试结果

记录日期：2026-11-19（Asia/Shanghai）。

## 环境

- 系统：Linux x86_64 容器，**不是 Windows 11 目标机**。
- .NET SDK：10.0.404；测试运行时：.NET 10.0.15。
- 测试框架：xUnit 2.9.3 / xUnit VSTest Adapter 3.1.4。
- 无真实麦克风、播放设备、Windows App SDK Runtime 或远程识别服务参与。

## 实际执行

```powershell
dotnet test tests/VoicePilot.Tests/VoicePilot.Tests.csproj --nologo
dotnet test tests/VoicePilot.Tests/VoicePilot.Tests.csproj --configuration Release --nologo
```

| 配置 | 通过 | 失败 | 跳过 | 总计 | 结果 |
| --- | ---: | ---: | ---: | ---: | --- |
| Debug | 23 | 0 | 0 | 23 | 通过，约 4 秒 |
| Release | 23 | 0 | 0 | 23 | 通过，约 4 秒 |

这两个命令还分别成功编译了 `VoicePilot.Core`、`VoicePilot.Audio`、`VoicePilot.Infrastructure`、`VoicePilot.Providers` 和 `VoicePilot.Tests`，启用了警告即错误。

## 覆盖范围

- PCM 格式、完整采样帧、音源端点和公开设置模型验证。
- 有界队列的字节上限、丢弃计数、读取/取消后的剩余数据释放。
- 重采样、单声道转换、精确 20 ms 分块、格式突变拒绝、原始时间戳断档重新锚定。
- 混音削波、10 ms 样本偏移对齐、连续帧消费、缺失流补零、输出节流、单路输入推进、长时间空闲后重同步。
- 临时/最终识别和译文顺序、正常停止、设备故障、启动失败清理。
- Provider 等待音频时取消，以及 Provider 故意忽略取消时的有限等待、错误通知和重启保护。
- JSON 设置往返、语言目录加载、敏感音频/令牌字段不存在、模拟内容标记。

测试全部使用内存音频和测试桩，**不证明 Windows WASAPI、WinUI 3、真实服务或目标机性能已验证**。

## GitHub Actions Windows 验证

[工作流运行 44448022772](https://github.com/chao2hang/voicepilot/actions/runs/44448022772) 在 `windows-2022` 上实际执行：

- Release 核心测试：**23/23 通过，0 失败、0 跳过**。
- `VoicePilot.App` WinUI/XAML 编译成功。
- `VoicePilot-v0.1.0-ci.5-win-x64.zip` 自包含打包、EXE 版本校验及 Artifact 上传成功。
- 这是 CI 编译结果，不是 Windows 11 实机 GUI、音频硬件或性能验收。

## 明确未通过/未执行的平台验证

Linux 交叉构建曾在 Windows `XamlCompiler.exe` 处报 `Exec format error`，托管 XAML 路径遇到 `MSB4061/MSB4027`；这些历史失败已由上述 GitHub Windows runner 成功构建取代。目标机仍建议执行：

```powershell
pwsh ./scripts/verify-windows.ps1
```

真实 CPU、内存、端到端延迟、连续运行时长均未采样；界面“识别时音频年龄”不是完整端到端延迟。
