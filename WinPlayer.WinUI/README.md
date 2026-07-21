# WinPlayer.WinUI

WinPlayer.WinUI 是一个基于 **WinUI 3** 和 Windows 原生 `MediaPlayer` 的桌面媒体播放器。项目采用 MVVM 组织主要播放逻辑，界面以视频画面为背景，标题栏、媒体文件夹、播放列表和控制栏以悬浮毛玻璃面板呈现。

## 主要功能

- 播放常见视频和音频文件
- 播放、暂停、上一项、下一项、快进和快退
- 拖动进度条定位播放位置
- 鼠标滚轮调整音量，支持静音和新媒体音量渐入
- 全屏播放，以及单击、双击播放区域的快捷操作
- 左侧媒体文件夹树和右侧播放列表
- 拖入媒体文件后添加到播放列表首项并立即播放
- 拖入文件夹后异步扫描媒体文件，避免阻塞界面
- 字幕轨道和音频轨道检测、菜单展示与切换
- 可拖动设置片头结束位置和片尾开始位置
- 自动跳过片头/片尾
- 保存和恢复播放位置、播放列表、媒体文件夹列表及窗口位置
- 侧边栏宽度调整、独立显示和自动隐藏动画
- 底部控制栏自动隐藏或固定显示
- 可配置单例运行；后续启动参数会转发给已有窗口
- 操作成功或失败时显示状态提示
- 基于 Win2D 的播放区域毛玻璃效果

支持的扩展名：

`.avi`、`.mp4`、`.mkv`、`.iso`、`.wmv`、`.vob`、`.mpg`、`.mpeg`、`.rmvb`、`.rm`、`.dat`、`.mov`、`.m4v`、`.webm`、`.ts`、`.mts`、`.m2ts`、`.mp3`、`.m4a`、`.wav`、`.flac`、`.ape`、`.aac`、`.ogg`。

> 实际解码能力取决于 Windows 当前安装的媒体编解码器。扩展名受到支持并不代表系统一定能够解码该文件中的视频、音频或字幕编码。

## 操作方式

| 操作 | 功能 |
| --- | --- |
| 单击播放区域 | 播放/暂停 |
| 双击播放区域 | 进入或退出全屏 |
| 在播放区域拖动 | 移动窗口 |
| 鼠标滚轮 | 调整音量 |
| 拖动进度条 | 调整播放位置 |
| 拖动片头/片尾标记 | 调整自动跳过范围 |
| 双击左侧文件夹节点 | 将该文件夹中的媒体文件载入播放列表 |
| 拖动侧栏中部调整块 | 调整侧栏宽度 |
| 单击侧栏中部调整块 | 关闭对应侧栏 |
| 播放区域右键 | 打开播放器快捷菜单 |

## 键盘快捷键

| 按键 | 功能 |
| --- | --- |
| `Space` | 播放/暂停 |
| `Enter` | 进入或退出全屏 |
| `←` | 后退，间隔可在选项中设置 |
| `→` | 快进，间隔可在选项中设置 |
| 小键盘 `+` | 播放下一项 |
| 小键盘 `-` | 播放上一项 |

## 启动参数

可以将一个或多个媒体文件路径作为命令行参数传入：

```powershell
WinPlayer.WinUI.exe "D:\Media\Example.mkv"
```

多个文件：

```powershell
WinPlayer.WinUI.exe "D:\Media\Episode01.mkv" "D:\Media\Episode02.mkv"
```

路径包含空格时必须使用双引号。启用“单例运行”后，再次执行命令会激活现有窗口，并把文件参数发送给现有播放器实例。

## 选项

选项窗口目前提供以下设置：

- 自动播放和自动进入全屏
- 默认音量
- 新媒体音量渐入开关及过渡时间
- 快进/快退间隔
- 片头和片尾跳过位置
- 底部控制栏固定显示
- 控制层自动隐藏延迟及动画时长
- 左、右侧栏宽度
- 毛玻璃模糊强度和色调不透明度
- 单例运行
- 播放位置保存、恢复及历史记录保留天数

## 本地数据

用户设置和播放状态保存在：

```text
%LOCALAPPDATA%\WinPlayer.WinUI\
```

| 文件 | 内容 |
| --- | --- |
| `settings.json` | 播放器选项、音量、侧栏宽度和窗口位置等 |
| `playlist.json` | 播放列表及左侧媒体文件夹列表 |
| `playback-history.json` | 各媒体文件的播放位置和更新时间 |
| `startup-error.log` | 未处理的 WinUI 启动异常信息 |

如需恢复默认配置，请先退出播放器，再备份或删除该目录中的对应 JSON 文件。

## 项目结构

```text
WinPlayer.WinUI/
├─ Controls/              自定义控件
├─ Effects/               Win2D 毛玻璃画刷
├─ Models/                配置、播放列表和媒体轨道模型
├─ Mvvm/                  属性通知与命令基础设施
├─ Services/              设置、播放列表和播放历史持久化服务
├─ ViewModels/            主播放器状态与业务逻辑
├─ MainWindow.xaml        主播放器界面
├─ MainWindow.xaml.cs     窗口交互、动画及系统集成
├─ SettingsDialog.xaml    播放器选项窗口
└─ App.xaml.cs            程序入口、单例及启动参数转发
```

## 开发环境

- Windows 10 1809（版本 17763）或更高版本
- Visual Studio，安装“.NET 桌面开发”和 Windows App SDK/WinUI 相关组件
- .NET 10 SDK
- Windows SDK 10.0.19041.0 或更高版本

项目的主要 NuGet 依赖：

- Microsoft.WindowsAppSDK 2.2.0
- Microsoft.Graphics.Win2D 1.4.0
- CommunityToolkit.WinUI.Controls.SettingsControls 8.2.251219

## 构建

在项目目录执行：

```powershell
dotnet restore
dotnet build WinPlayer.WinUI.csproj -c Debug -p:Platform=x64
```

也可以使用 Visual Studio 打开 `WinPlayer.WinUI.csproj`，选择与电脑架构一致的平台后运行：

- 普通 Intel/AMD 电脑：`x64`
- 32 位 Windows：`x86`
- Windows on ARM 设备：`ARM64`

不要混用配置平台和目标运行时，例如 `Release | ARM64` 必须对应 `win-arm64`。

## 发布

项目已经提供以下发布配置：

```text
Properties/PublishProfiles/win-x86.pubxml
Properties/PublishProfiles/win-x64.pubxml
Properties/PublishProfiles/win-arm64.pubxml
```

发布 x64 版本：

```powershell
dotnet publish WinPlayer.WinUI.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true
```

发布后必须保留整个 `publish` 目录，不能只复制 EXE。WinUI、.NET 运行时、PRI/XBF 资源和图标等文件都是程序运行所需内容。

当前项目配置为：

- 自包含部署
- 不生成单文件
- 不启用裁剪
- 不启用 ReadyToRun
- 发布后补充复制 `App.xbf`、`MainWindow.xbf` 和 `WinPlayer.WinUI.pri`

这些设置用于降低 WinUI 未打包发布时因运行时、反射元数据或 XAML/PRI 资源缺失而无法启动的风险。

## 故障排查

### 发布程序提示“此应用无法在你的电脑上运行”

检查发布架构是否与目标电脑一致。大多数 Intel/AMD Windows 电脑应使用 `Release | x64` 和 `win-x64`，不能运行 `win-arm64` 版本。

### 发布后双击没有反应

1. 确认复制的是完整发布目录。
2. 检查 `WinPlayer.WinUI.pri`、`App.xbf`、`MainWindow.xbf` 和 `Images\player.ico` 是否存在。
3. 查看 `%LOCALAPPDATA%\WinPlayer.WinUI\startup-error.log`。
4. 确认没有开启裁剪或单文件发布。

### 某些视频、字幕或音轨无法使用

播放器使用 Windows 原生媒体框架，支持情况取决于系统编解码器、媒体容器以及轨道格式。可先使用系统“媒体播放器”验证同一文件是否能够正常解码。

## 当前状态

项目主要播放器功能已经实现，适合继续进行兼容性测试、异常处理完善和发布验证。正式分发前建议至少在 x64 和目标 Windows 版本上测试：启动、连续切换媒体、字幕/音轨、全屏、拖放、配置恢复及自包含发布。
