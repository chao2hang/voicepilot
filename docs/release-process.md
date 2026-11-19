# GitHub 构建与版本发布

## 自动构建的内容

`.github/workflows/release.yml` 在 Windows runner 上执行：

1. 还原固定版本的稳定 NuGet 依赖。
2. 运行 Release 核心测试。
3. 使用 .NET 10 SDK 编译 WinUI 3 应用。
4. 发布 `win-x64`、.NET 自包含、Windows App SDK 自包含的**目录式应用**。
5. 检查 EXE 文件版本及关键运行时文件，生成 ZIP 和 SHA-256。
6. 上传 GitHub Actions Artifact；版本标签触发时同时创建/更新 GitHub Release。

WinUI 3 不能按本项目当前形态安全地发布成单个无依赖 EXE。交付 ZIP 内的 `VoicePilot.exe` 是启动文件，必须保留同目录 DLL。无需另装 .NET Runtime 或 Windows App SDK Runtime；仍可能需要系统安装当前受支持的 Visual C++ Redistributable。当前没有代码签名证书，不会声称发布包已签名或绕过 SmartScreen。

## 正式/预发布版本

提交更新到 `main` 后，在确认准备发布的提交上创建带注释标签：

```powershell
git switch main
git pull --ff-only
# 例：正式版本
git tag -a v0.1.0 -m "VoicePilot v0.1.0"
git push origin v0.1.0
```

预发布示例：

```powershell
git tag -a v0.2.0-rc.1 -m "VoicePilot v0.2.0-rc.1"
git push origin v0.2.0-rc.1
```

标签格式：`vMAJOR.MINOR.PATCH`，可带 SemVer 预发布后缀。标签中的版本写入 EXE 的程序集/文件/信息版本。带 `-` 的标签自动标记为 GitHub 预发布。

推送标签只会为**该标签指向的提交**触发一次构建。普通 `main` 提交不会自动创建版本。重新推送同一个标签可能更新同一个 Release；正式发布后应使用新版本标签，而不是覆盖历史版本。

## 不发布版本的测试构建

在 GitHub **Actions → Windows release → Run workflow** 中：

- `version` 填写唯一的测试版本，如 `v0.1.0-ci.1`。
- 保持 `publish_release=false`。

这样只构建并上传 Actions Artifact，不创建 Release。保留期为 14 天。只有 `v*` 标签推送或显式启用 `publish_release` 才会发布。

## 本机复现

在 Windows PowerShell 7 中：

```powershell
dotnet test tests/VoicePilot.Tests/VoicePilot.Tests.csproj -c Release
dotnet restore src/VoicePilot.App/VoicePilot.App.csproj -r win-x64
./scripts/publish-windows.ps1 -Version v0.1.0 -Architecture x64
```

输出位于 `artifacts/release/`，发布目录位于 `artifacts/publish/`。校验：

```powershell
Get-FileHash artifacts/release/VoicePilot-v0.1.0-win-x64.zip -Algorithm SHA256
```

## 发布边界

- CI 测试和 Windows 编译证明源代码可构建，不代表真实设备、服务或性能验收。
- 默认没有真实识别/翻译后端；模拟 Provider 输出必须继续明确标注。
- 若后续需要 arm64，应增加独立构建任务和对应 ZIP，不能将 x64 包标记为通用包。
- 正式发布前仍应按 `docs/windows-acceptance.md` 完成实机验证。
