<p align="center">
  <img src="WinPlayer.WinUI/Images/player.c.png" alt="WinPlayer.WinUI" width="140">
</p>

<h1 align="center">WinPlayer.WinUI</h1>

<p align="center">
  基于 <b>WinUI 3</b> 与 Windows 原生 <code>MediaPlayer</code> 的桌面媒体播放器：<br>
  视频画面铺满窗口，标题栏、媒体文件夹、播放列表与控制栏以悬浮毛玻璃面板呈现。
</p>

<p align="center">
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10-512BD4">
  <img alt="WinUI" src="https://img.shields.io/badge/WinUI-3-0F6CBD">
  <img alt="License" src="https://img.shields.io/badge/license-MIT-green">
</p>

---

## 简介

WinPlayer.WinUI 是一个面向 Windows 10/11 的本地媒体播放器。它不做解码工作，而是把播放交给
Windows 原生媒体框架，自己专注在**播放体验与界面**上：无边框沉浸式窗口、Win2D 毛玻璃面板、
侧栏自动隐藏、双击全屏、片头片尾自动跳过、断点续播，以及一套完全独立的**隐私播放模式**。

项目采用 MVVM 组织代码，界面层（`MainWindow` / `SettingsWindow`）只负责渲染与交互，
播放状态、播放列表、持久化与系统集成都收敛在 `ViewModels` 与 `Services` 中。

## 界面预览

主界面：视频画面铺满窗口，标题栏、媒体文件夹、播放列表与控制栏都是悬浮的毛玻璃面板。

<p align="center">
  <img src="docs/screenshots/main.png" alt="WinPlayer.WinUI 主界面" width="760">
</p>

选项窗口：独立窗口，不随主窗口缩小而被裁切（图中处于隐私模式，因此设置保存到 `settings.b.json`）。

<p align="center">
  <img src="docs/screenshots/settings.png" alt="WinPlayer.WinUI 选项窗口" width="600">
</p>

## 功能特性

**播放**

- 播放常见视频与音频文件，支持暂停、上一项 / 下一项、快进快退、拖动进度条定位
- 鼠标滚轮调整音量，支持静音，新媒体播放时音量渐入
- 全屏播放；单击播放区域切换播放/暂停，双击进入或退出全屏，拖动播放区域移动窗口
- 字幕轨道与音频轨道检测、菜单展示与切换；支持外挂字幕加载
- 可拖动设置片头结束位置与片尾开始位置，自动跳过片头/片尾
- 保存并恢复每个文件的播放位置，支持历史记录保留天数与记录清理

**界面**

- 视频画面作为窗口背景，标题栏、文件夹树、播放列表、控制栏均为悬浮毛玻璃面板
- 左侧媒体文件夹树与右侧播放列表，支持拖入文件或文件夹（异步扫描，不阻塞界面）
- 侧栏宽度可拖动调整，可独立显示 / 隐藏，带自动隐藏动画
- 底部控制栏可自动隐藏或固定显示，延迟与动画时长可调
- 毛玻璃模糊强度与色调不透明度可调
- 独立的选项窗口，导航分为「播放 / 界面 / 记录与启动 / 诊断日志」四页

**系统集成**

- 可配置单例运行：后续启动的参数会转发给已有窗口
- 文件关联注册与移除（仅写当前用户注册表，无需管理员权限），并提供系统「默认应用」入口
- 记录窗口位置，下次启动恢复
- 任务栏图标与应用标识按模式区分

**HDR 与杜比视界**

- 「直通呈现」模式：把画面交回系统合成器输出，交给系统处理 HDR 元数据
- 内置 **libmpv 杜比视界引擎**：用 libmpv 软件渲染 API + libplacebo 滤镜链完成杜比视界重塑与色调映射
- HDR / 杜比诊断：一键输出当前呈现路径、系统降级原因、显示器高级颜色能力与视频轨道的杜比视界标记
- 可配置外部播放器，把系统无法正确呈现的内容交给它打开

**隐私播放模式**

- 由启动参数决定整个进程的运行模式，播放器本身不保存任何「隐私开关」
- 与普通模式**数据完全隔离**：独立的记录文件、设置文件、日志、单例互斥体、激活管道与任务栏标识
- 隐私模式不改动播放列表与媒体文件夹列表，不修改注册表，日志不记录媒体完整路径
- 两种模式可同时运行，各自独立窗口与任务栏按钮
- ⚠️ **隔离不等于加密**：记录与设置均为明文 JSON

## 支持的格式

```text
.avi  .mp4  .mkv  .iso  .wmv  .vob  .mpg  .mpeg  .rmvb  .rm  .dat  .mov
.m4v  .webm .ts   .mts  .m2ts .mp3  .m4a  .wav  .flac  .ape  .aac  .ogg
```

> 扩展名受支持不代表系统一定能解码。实际解码能力取决于本机已安装的媒体编解码器——
> 缺少 HEVC / 杜比视界扩展时，对应内容会无法播放或呈现异常。

## 环境要求

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 1809（版本 17763）或更高；Windows 11 体验最佳 |
| 开发环境 | .NET 10 SDK |
| SDK | Windows SDK 10.0.19041.0 或更高 |
| 开发工具（可选） | Visual Studio，安装「.NET 桌面开发」与 Windows App SDK / WinUI 组件 |

主要 NuGet 依赖：

| 包 | 版本 |
| --- | --- |
| Microsoft.WindowsAppSDK | 2.2.0 |
| Microsoft.Graphics.Win2D | 1.4.0 |
| CommunityToolkit.WinUI.Controls.SettingsControls | 8.2.251219 |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2270 |

## 快速开始

### 获取源码并构建

```powershell
git clone https://github.com/<your-account>/WinPlayer.WinUI.git
cd WinPlayer.WinUI/WinPlayer.WinUI

dotnet restore
dotnet build WinPlayer.WinUI.csproj -c Debug -p:Platform=x64
```

`-p:Platform` 必须与本机架构一致：普通 Intel/AMD 电脑用 `x64`，32 位 Windows 用 `x86`，
Windows on ARM 设备用 `ARM64`。

也可以用 Visual Studio 直接打开 `WinPlayer.WinUI/WinPlayer.WinUI.csproj`，选好平台后运行。

### 发布

仓库已提供三份发布配置：`Properties/PublishProfiles/win-{x86,x64,arm64}.pubxml`。

```powershell
dotnet publish WinPlayer.WinUI.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true
```

> **发布后必须保留整个 `publish` 目录**，不能只复制 EXE。WinUI、.NET 运行时、PRI/XBF
> 资源与图标都是运行所需内容。项目为自包含部署，故**不启用**单文件、裁剪与 ReadyToRun。

### 运行

```powershell
WinPlayer.WinUI.exe                      # 直接启动
WinPlayer.WinUI.exe "D:\Media\Movie.mkv" # 启动并播放指定文件（可传多个）
```

命令行参数、快捷键与隐私模式的完整说明见项目文档。

## 本地数据

用户设置与播放状态保存在：

```text
%LOCALAPPDATA%\WinPlayer.WinUI\
```

| 文件 | 内容 |
| --- | --- |
| `settings.json` | 普通模式：选项、音量、侧栏宽度与窗口位置 |
| `playlist.json` | 播放列表与左侧媒体文件夹列表（两种模式共用，隐私模式只读） |
| `playback-history.json` | 普通模式：各文件的播放位置与更新时间 |
| `settings.b.json` / `history.b.json` | 隐私模式：独立的设置与播放记录 |
| `Logs\winplayer.log` / `Logs\winplayer.b.log` | 各自的诊断日志（JSON Lines，单文件超过 5 MB 轮换） |
| `startup-error.log` / `startup-error.b.log` | 各自的未处理启动异常信息 |

普通模式文件无后缀，隐私模式带 `.b` 后缀。**两种模式数据完全隔离**，且均为明文 JSON。
如需恢复默认配置，先退出播放器，再备份或删除该目录下对应的 JSON 文件。

## 项目结构

```text
WinPlayer.WinUI/                       仓库根目录
├─ WinPlayer.WinUI/                    播放器主工程
│  ├─ App.xaml.cs                      入口、单例、启动参数转发
│  ├─ AppMode.cs                       普通 / 隐私模式判定
│  ├─ CursorManager.cs                 全屏自动隐藏鼠标指针
│  ├─ MainWindow.xaml(.cs)             主界面与窗口交互
│  ├─ SettingsWindow.xaml(.cs)         独立选项窗口
│  ├─ Effects/                         Win2D 毛玻璃画刷
│  ├─ Models/                          设置、播放列表、媒体轨道等模型
│  ├─ Mvvm/                            属性通知与命令基础设施
│  ├─ Services/                        持久化、日志、文件关联、HDR 诊断、libmpv 引擎
│  ├─ ViewModels/                      主播放状态与业务逻辑
│  ├─ Images/                         图标与图片资源
│  ├─ Cursors/                         透明光标资源
│  └─ docs/                            设计与工具文档
├─ WinPlayer.WinUI (Package)/          MSIX 打包工程（.wapproj）
├─ .gitattributes / .editorconfig      换行与代码风格约定
└─ LICENSE                             MIT
```

## 文档

| 文档 | 内容 |
| --- | --- |
| [`WinPlayer.WinUI/README.md`](WinPlayer.WinUI/README.md) | 完整使用说明：快捷键、启动参数、选项、文件关联、故障排查 |
| [`WinPlayer.WinUI/docs/双模式隔离-开发方案.md`](WinPlayer.WinUI/docs/双模式隔离-开发方案.md) | 普通 / 隐私双模式完全隔离的设计与实施 |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | 构建、代码风格与提交约定 |
| [`docs/Git工作流与备份.md`](docs/Git工作流与备份.md) | 分支、提交、标签与远程备份流程 |

## 已知限制

- 播放依赖 Windows 原生媒体框架，**不内置解码器**；支持情况取决于系统已安装的编解码器。
- 自绘呈现路径把帧服务器输出绘制到 8 位表面，HDR 元数据与杜比视界信息会丢失，
  因此 HDR / 杜比内容应使用「直通呈现」或 libmpv 杜比视界引擎。
- 杜比视界 Profile 5（单层 IPT）在系统媒体框架下必然发绿/发紫，需依赖 libmpv 引擎。
- libmpv 杜比视界引擎需要另行获取 `libmpv-2.dll`，**仓库不包含**该二进制。
- 文件关联的「双击打开」无法附加隐私模式参数，需通过快捷方式或命令行指定。
- 项目目前主要在 x64 Windows 上验证，x86 与 ARM64 缺少系统性测试。

## 参与贡献

欢迎提交 Issue 与 Pull Request。构建方式、代码风格与提交信息约定见 [`CONTRIBUTING.md`](CONTRIBUTING.md)。

## 第三方组件

- [Windows App SDK](https://github.com/microsoft/WindowsAppSDK)（MIT）
- [Win2D](https://github.com/microsoft/Win2D)（MIT）
- [Windows Community Toolkit](https://github.com/CommunityToolkit/Windows)（MIT）
- [libmpv](https://github.com/mpv-player/mpv) / libplacebo —— **可选、不随仓库分发**。
  使用内置杜比视界引擎时由使用者自行提供，需遵守其 LGPL/GPL 许可条款。

## 许可证

本项目以 [MIT 许可证](LICENSE) 发布。
