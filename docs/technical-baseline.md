# 技术基线

| 组件 | 选择 | 状态 |
| --- | --- | --- |
| .NET | 10 LTS；`global.json` 从 10.0.100 roll-forward 到最新 10.0.x feature band | 官方 WinUI 快速入门明确使用 .NET 10 SDK |
| UI | WinUI 3 / Windows App SDK 2.7.0 | Stable，发布于 2026-11-17；不使用 Preview/Experimental |
| 操作系统 | Windows 11 | Windows App SDK 2.x 官方支持矩阵包含 Windows 11 |
| 音频 | NAudio 2.2.1 的稳定 WASAPI 封装 | 共享模式麦克风与端点级 Loopback |
| MVVM | CommunityToolkit.Mvvm 8.4.2 | 稳定版；支持 C# 14 partial property，满足 WinUI/CsWinRT AOT 兼容要求 |

官方依据：

- [WinUI 快速入门（.NET 10 SDK）](https://learn.microsoft.com/en-us/windows/apps/get-started/start-here)
- [Windows App SDK 发布渠道与生命周期](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels)
- [Windows App SDK 2.7.0 下载](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)
- [Windows 版本支持矩阵](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/support)

这是文档兼容性确认，不代替目标 Windows 11 机器上的构建、签署、安装和运行验证。所有依赖版本集中锁定在 `Directory.Packages.props`，没有预发布包。
