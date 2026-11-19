# VoicePilot

面向 Windows 11 的 WinUI 3 桌面实时语音翻译客户端。本项目为全新独立实现，不依赖已有业务项目或服务。

> **交付阶段：客户端、音频链路和可替换服务结构。真实识别/翻译待后端协议。** 默认提供程序不会产生识别结果；`开发联调（模拟）`只显示固定模拟文本。GitHub Actions 已验证 Windows x64 编译与自包含打包；真实设备、服务与性能验收仍待实机执行，详见 [验证状态](docs/verification.md)。

## 功能

- 枚举活动麦克风与播放设备，保留稳定 WASAPI 端点 ID。
- 麦克风、指定播放设备 WASAPI Loopback、双路混合三种模式。
- 双路分别重采样/降混为单声道；单调时钟对齐、有限等待、削波限幅，输出单路 16 kHz PCM16 / 20 ms。
- 显式开始/停止、醒目采集状态、设备变更通知、断开后重新选择。
- 音频回调仅入队；两层缓冲均有界；处理与网络提供程序不在 UI 线程或音频回调执行。
- 显示临时识别、最终结果和对应译文；语言及术语可配置。
- 设置不保存音频或服务令牌；识别与译文历史仅保留在内存，最多 500 条。

## 下载与自动发布

GitHub Actions 在推送 `v*` 版本标签时自动测试、编译 Windows x64 应用，并将完整 ZIP 发布到 [Releases](https://github.com/chao2hang/voicepilot/releases)。完整解压后运行其中的 `VoicePilot.exe`，不要只复制 EXE。包内包含 .NET 10 和 Windows App SDK 自包含组件；无需另装这两个 Runtime。

可在 Actions 手动执行测试构建而不创建 Release。版本发布步骤、标签规则、SHA-256 校验及未签名说明见 [发布流程](docs/release-process.md)。

## 在 Windows 11 构建运行

需要：

1. 受支持的 Windows 11。当前解包运行方式不要求开发者模式或管理员权限。
2. [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（建议最新稳定补丁）。
3. 支持 .NET 10 的当前稳定版 [Visual Studio](https://visualstudio.microsoft.com/downloads/)，安装 **WinUI 应用程序开发**工作负载。
4. [Windows App SDK 2.7.0 x64/arm64 Runtime](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)。
5. 当前 Microsoft Visual C++ Redistributable。

命令行（PowerShell 7）：

```powershell
dotnet restore
# 核心单元测试（无真实音频设备）
dotnet test tests/VoicePilot.Tests/VoicePilot.Tests.csproj
# 构建并启动 WinUI 3
pwsh ./scripts/run-windows.ps1 -Architecture x64 -Configuration Debug
```

也可打开 `VoicePilot.slnx`，将 `VoicePilot.App` 设为启动项目，选择 x64 或 arm64 后 F5。

默认 `未接入服务（仅采集音频）` 不生成识别；如需验证文本界面，选择 `开发联调（模拟，非真实识别）`。它**不是**真实语音服务。

## 项目

| 路径 | 责任 |
| --- | --- |
| `src/VoicePilot.App` | Fluent UI、XAML、MVVM、依赖注入 |
| `src/VoicePilot.Core` | 音频/服务契约、设置模型、有界队列、会话编排 |
| `src/VoicePilot.Audio` | WASAPI 设备与采集、转换、混音、生命周期 |
| `src/VoicePilot.Providers` | 未配置和明确标注的联调模拟实现 |
| `src/VoicePilot.Infrastructure` | JSON 偏好设置、语言目录 |
| `tests` | 单元和故障模拟测试 |
| `scripts` | Windows 构建启动与进程性能采样 |
| `docs` | 技术基线、提供程序协议、验收与验证状态 |

## 设置与隐私

设置位于 `%LOCALAPPDATA%\VoicePilot\settings.json`，包括设备 ID、语言、音源、增益和术语。没有后端地址、认证或敏感信息字段。原始音频不写盘，日志不记录文本内容；关闭窗口会停止采集。

系统声音模式是**端点级 WASAPI Loopback**：捕获所指定播放端点输出的系统混音，不是麦克风采集，也不按单个进程筛选。若系统或音频驱动禁止 Loopback，界面会显示启动/采集中断错误。

如果麦克风没有输入，请检查 Windows 11“设置 → 隐私和安全性 → 麦克风”是否允许桌面应用访问。无需管理员权限；不要使用管理员身份规避隐私或独占模式限制。

[技术基线](docs/technical-baseline.md) · [后端接入契约](docs/provider-contract.md) · [验收步骤](docs/windows-acceptance.md) · [验证状态](docs/verification.md) · [自动化测试结果](docs/test-results.md)
