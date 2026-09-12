# Images 资源说明

`WinPlayer.WinUI/Images/` 同时存放**运行时资源**与**图标设计源图**。运行时资源由
`WinPlayer.WinUI.csproj` 显式声明并复制到输出目录；源图只在重新生成图标时作为输入使用，
已在 csproj 中排除，不参与构建。

## 运行时资源

| 文件 | 用途 | 引用位置 |
| --- | --- | --- |
| `player.png` | 主界面与选项窗口左上角的小图标（200×200） | `MainWindow.xaml`、`SettingsWindow.xaml` 的 `Source="/images/player.png"` |
| `player.ico` | 普通模式的窗口图标、任务栏图标与可执行文件图标 | csproj 的 `ApplicationIcon` 与 `Content`；运行时由 `AppMode.IconFileName` + `AppWindow.SetIcon` 读取 |
| `player.b.ico` | 隐私模式的窗口与任务栏图标（多尺寸 ICO） | csproj 的 `Content`；`AppMode.IconFileName` 在隐私模式返回该文件名 |

> 两个 `.ico` 都必须与可执行文件同目录存在，否则窗口图标会回退为系统默认图标。

## 图标设计源图

| 文件 | 说明 |
| --- | --- |
| `player.b.png` | 隐私模式图标的源图（1536×1536）。**`docs/tools/build-privacy-icon.ps1` 的默认输入**，删除它会导致隐私图标无法重新生成。 |
| `player.c.png` | 程序 Logo 源图（1536×1536），用于仓库首页展示；不是构建产物，也不参与运行。 |

两者都是正方形 PNG，尺寸与格式符合 `build-privacy-icon.ps1` 的要求。

## 重新生成图标

把源图重新生成为标准多尺寸 ICO（16/20/24/32/40/48 用 32bpp DIB 帧，64/96/128/256 用 PNG 帧）：

```powershell
# 在项目目录（WinPlayer.WinUI\WinPlayer.WinUI）下执行
pwsh -File docs\tools\build-privacy-icon.ps1

# 也可以显式指定输入与输出
pwsh -File docs\tools\build-privacy-icon.ps1 -Source Images\player.b.png -Target Images\player.b.ico
```

脚本会打印每一帧的尺寸、类型与字节数，以及最终 ICO 的总大小。

## Cursors/

`Cursors/blank.cur` 是全屏自动隐藏鼠标指针时使用的全透明光标。csproj 通过 `<Link>` 把它
固定复制为可执行文件旁的 `blank.cur`，由 `CursorManager` 用 `LoadCursorFromFile` 读取。

## 已移除的资源

`Images/` 曾包含 `music.ico`、`music.png`、`video.png`、`wind.png`。它们是从早期 WPF 版
播放器复制过来的遗留文件，在本项目中没有任何引用——其中 `music.png` / `video.png` /
`wind.png` 还会被默认内容通配符自动复制进每次构建与发布输出。这些文件已删除（约 731 KB）。
