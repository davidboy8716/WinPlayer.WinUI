# 参与贡献

感谢你有兴趣改进 WinPlayer.WinUI。本文说明如何搭建环境、代码风格约定与提交规范。

## 开发环境

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 1809（17763）或更高 |
| SDK | .NET 10 SDK、Windows SDK 10.0.19041.0 或更高 |
| 开发工具 | Visual Studio（含「.NET 桌面开发」与 Windows App SDK / WinUI 组件）或 VS Code + C# Dev Kit |

## 构建与运行

```powershell
git clone https://github.com/<your-account>/WinPlayer.WinUI.git
cd WinPlayer.WinUI/WinPlayer.WinUI

dotnet restore
dotnet build WinPlayer.WinUI.csproj -c Debug -p:Platform=x64
dotnet run --project WinPlayer.WinUI.csproj -c Debug -p:Platform=x64
```

`-p:Platform` 必须与本机架构一致：

- 普通 Intel/AMD 电脑 → `x64`
- 32 位 Windows → `x86`
- Windows on ARM 设备 → `ARM64`

不要混用配置平台和目标运行时：`Release | ARM64` 必须对应 `-r win-arm64`。

## 代码风格

仓库根目录的 [`.editorconfig`](.editorconfig) 已定义缩进、换行与 C# 偏好，Visual Studio、
VS Code 与 Rider 都会自动读取，无需手工配置。除此之外：

- **注释与文档使用中文**，与现有代码保持一致；注释解释「为什么」，不复述「做了什么」。
- 新增类型、公开成员请写 XML 文档注释（`///`）。
- 遵循现有分层：界面与交互留在 `MainWindow` / `SettingsWindow`，播放状态与业务逻辑放在
  `ViewModels` / `Services`，模型放在 `Models`。
- 不要在界面代码里直接读写文件或注册表，统一通过 `Services` 中的服务。
- 异常不要静默吞掉：除确有理由并已写明注释的场景外，请通过 `AppLogService` 记录。
- 新增受支持的媒体扩展名时，只改 `Models/SupportedMedia.cs`——拖放扫描、文件选择器与
  文件关联都从那里读取，不要在多处各写一份列表。

## 隐私与数据

本项目包含一套与普通模式数据完全隔离的隐私播放模式。改动相关代码时请注意：

- 新增任何持久化文件、日志文件、进程名或 IPC 通道，都必须带上 `AppMode.Suffix`
  （普通模式为空串，隐私模式为 `.b`），否则两种模式会互相污染。
- 隐私模式不得写注册表、不得记录媒体完整路径、不得改动播放列表与媒体文件夹列表。
- 详见 [`WinPlayer.WinUI/docs/双模式隔离-开发方案.md`](WinPlayer.WinUI/docs/双模式隔离-开发方案.md)。

## 提交规范

- 一个提交只做一件事，改动范围尽量小。
- 提交信息用中文，第一行简明说明做了什么，必要时空一行再写原因：

  ```text
  修复隐私模式日志轮换写入普通日志文件

  轮换目标文件名漏掉了 AppMode.Suffix，导致隐私模式日志
  与普通模式共用同一个 .previous.log，破坏双模式隔离。
  ```

- **提交前请务必确认没有把敏感信息带进仓库**（构建产物、IDE 缓存、密钥、本地运行数据、
  个人账号与绝对路径）。完整清单见 [`docs/Git工作流与备份.md`](docs/Git工作流与备份.md)。

## 提交 Pull Request

1. 从 `main` 切出分支，例如 `fix/folder-tree-lazy-load`。
2. 完成改动并**确保 `dotnet build` 通过**；若改动涉及界面，请附上截图或说明验证方式。
3. 在 PR 描述中说明：解决了什么问题、怎么验证、是否有已知限制。
4. 不要在 PR 中夹带与主题无关的格式化改动。

## 报告问题

提交 Issue 时请附上：

- Windows 版本、程序架构（x64 / x86 / ARM64）与获取方式（自建 / 发布包）
- 复现步骤与预期行为
- 相关日志：`%LOCALAPPDATA%\WinPlayer.WinUI\Logs\winplayer.log`
  （隐私模式为 `winplayer.b.log`）与 `startup-error.log`

> 日志采用 JSON Lines 格式。**提交前请自行检查并删除其中包含的个人路径与文件名。**
