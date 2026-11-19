# 验证状态

## 已执行（Linux x64，.NET 10.0.404）

- 官方稳定版兼容基线核对与 NuGet 版本锁定。
- `VoicePilot.Core`、`VoicePilot.Audio`、`VoicePilot.Infrastructure`、`VoicePilot.Providers` 的 .NET 编译。
- 跨平台单元测试：最终 Debug 23/23、Release 23/23 通过，0 失败、0 跳过，详见 [实际测试结果](test-results.md)。覆盖音频帧验证、有界队列、重采样与分块、混音削波/偏移/连续消费/空闲重同步、设置往返、语言配置、会话生命周期及 Provider 停止超时。
- 这些测试使用内存音频及测试桩，**不证明 Windows 音频设备或 WinUI 3 已运行**。

## GitHub Actions Windows 验证

- [工作流运行 44448022772](https://github.com/chao2hang/voicepilot/actions/runs/44448022772) 在 `windows-2022` 上成功执行 Release 核心测试 23/23、WinUI/XAML 编译、EXE 文件版本检查、.NET/Windows App SDK 自包含打包和 Artifact 上传。
- 构建中实际发现并修复了 WinUI 枚举名遮蔽、MVVM partial-property/AOT 兼容和集合索引问题。MVVM Toolkit 已升级到稳定版 8.4.2，保持 C# 14，未启用 preview 或抑制兼容性警告。
- CI 成功不代表 Windows 11 实机 GUI、音频硬件、真实服务或性能验收。最新状态以 [Actions](https://github.com/chao2hang/voicepilot/actions/workflows/release.yml) 为准。

## 当前未验证/待外部条件

| 功能 | 状态 |
| --- | --- |
| WinUI 3 的 Windows 编译与 XAML 编译 | 已在 GitHub `windows-2022` 成功验证 |
| WinUI 3 的 Windows 11 实机启动、交互及 UI 自动化 | 尚未执行 |
| 麦克风枚举、指定端点 Loopback、格式协商 | 代码已实现，未在 Windows 11 设备验证 |
| 三种音源模式、热插拔/禁用/默认设备切换 | 代码和模拟测试具备，硬件恢复未验证 |
| 实际 CPU、内存、端到端延迟、连续运行时长 | 未在目标机器执行，无达标结论 |
| 真实语音识别与翻译 | 后端协议未提供；只有接口和明确标注的模拟实现 |
| 安装包、签名、自动更新 | 本阶段未实现；提供包含 .NET/Windows App SDK 的自包含解包版，未进行代码签名 |

Linux 交叉构建曾停在 Windows `XamlCompiler.exe` 的 `Exec format error`；上述 GitHub Windows runner 已完成成功构建，但不等于真实运行验证。最终以 Windows 11 上 `docs/windows-acceptance.md` 的实测记录为准。

## 资源释放边界

正常路径先停止采集、完成输入队列，再等待最多 3 秒的最终结果；Provider 未退出时取消并再等待 1 秒。若仍不响应，会报告 `PROVIDER_STOP_TIMEOUT`，保留其后退清理观察并阻止不安全重启。采集停止等待本身最多 4 秒；超时仅表示调用方不再无限等待，**不证明驱动线程已退出或句柄已释放**。NAudio/WASAPI 的同步 `Dispose()` 仍依赖驱动返回，不能承诺在任意故障驱动下严格有界。
