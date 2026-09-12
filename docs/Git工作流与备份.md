# Git 工作流与备份

本文说明本仓库的日常 Git 流程：首次上传、提交、打版本标签、以及在另一台电脑恢复项目。

> 文中用 `<your-account>` 和 `<仓库路径>` 表示需要替换成你自己的账号名与本地目录，
> 请勿把个人账号、邮箱或本机绝对路径写进仓库里的任何文件。

## 一、首次上传（每个仓库只需做一次）

### 1. 进入仓库目录

```powershell
cd <仓库路径>
```

### 2. 确认 Git 可用

```powershell
git --version
```

### 3. 配置提交者身份

只为当前仓库配置（推荐，避免影响机器上的其他仓库）：

```powershell
git config user.name "<你的提交者名称>"
git config user.email "<你的邮箱>"
```

> 提交者姓名与邮箱会写进每一条提交记录并公开，若仓库是公开的，请使用你愿意公开的邮箱。

### 4. 初始化并提交

```powershell
git init
git branch -M main
git add .
git status          # 提交前务必看一眼，确认没有不该入库的文件
git commit -m "初始化 WinPlayer.WinUI 播放器项目"
```

### 5. 关联远程仓库并推送

```powershell
git remote add origin https://github.com/<your-account>/WinPlayer.WinUI.git
git push -u origin main
```

输出中出现下面两行即表示推送成功：

```text
main -> main
branch 'main' set up to track 'origin/main'
```

## 二、日常提交

每完成一批改动：

```powershell
git status
git diff            # 可选：确认改动内容
git add .
git commit -m "说明本次修改内容"
git push
```

提交信息建议写得能独立说明问题，例如：

```text
修复目录树按需加载
完善外挂字幕和运行日志
调整播放器控制栏布局
```

## 三、提交前检查清单

公开仓库一旦推送，内容就很难真正收回（历史里的旧提交仍然存在）。每次 `git add .` 之前请确认：

- [ ] `git status` 的输出里**没有** `bin/`、`obj/`、`publish/`、`in/`、`.vs/` 等构建产物
- [ ] 没有 `*.user`、`*.assets.cache` 这类包含**本机用户名或绝对路径**的 IDE 缓存文件
- [ ] 没有 `*.pfx` / `*.p12` / `*.snk` 等签名与密钥文件
- [ ] 没有 `settings.json`、`playlist.json`、`playback-history.json` 等本地运行数据
- [ ] 没有 `*.log` 日志文件，也没有 `.tools/`、`libmpv-2.dll` 等第三方二进制
- [ ] 提交信息与代码里没有真实姓名、邮箱、内网地址、机器名等个人信息

检查漏网文件：

```powershell
# 列出即将提交的全部文件
git status --porcelain

# 检查某个文件是否已被忽略规则覆盖
git check-ignore -v <路径>

# 在整个仓库中搜索可能的个人信息（按需调整关键词）
git grep -n -I -E "C:\\\\Users\\\\|password|secret|api[_-]?key"
```

如果敏感文件**已经提交但还没推送**：

```powershell
git rm --cached <文件>          # 从版本控制移除，保留本地文件
```

然后把该文件补进 `.gitignore` 再提交。

如果已经推送，仅用 `git rm --cached` 只能保证**当前版本**不再包含它，历史提交里仍然存在。
要彻底清除需重写历史（`git filter-repo` / `git filter-branch`）并强制推送，
这会改变提交哈希、影响所有协作者的克隆，操作前请务必自行备份。

## 四、打版本标签

当某个版本经过测试可以发布时：

```powershell
git tag -a v1.0.0 -m "首个稳定版本"
git push origin v1.0.0
```

后续版本建议遵循语义化版本：

```text
v1.0.1   修复类
v1.1.0   新增功能
v2.0.0   不兼容变更
```

推送标签后，可在 GitHub 的 Releases 页面基于该标签创建发行版并附上发布包。

## 五、检查备份状态

```powershell
git status
```

出现下面的输出表示本地已提交且与远端同步：

```text
Your branch is up to date with 'origin/main'.
nothing to commit, working tree clean
```

查看远程地址：

```powershell
git remote -v
```

## 六、在另一台电脑恢复项目

```powershell
git clone https://github.com/<your-account>/WinPlayer.WinUI.git
cd WinPlayer.WinUI/WinPlayer.WinUI
dotnet restore
```

随后用 Visual Studio 打开 `WinPlayer.WinUI.csproj`，或参照根目录 `README.md` 用命令行构建。

## 七、常见提示

### `Reinitialized existing Git repository`

该目录已经是 Git 仓库。重复执行 `git init` 不会删除代码。

### `Author identity unknown`

尚未配置提交者身份：

```powershell
git config user.name "<你的提交者名称>"
git config user.email "<你的邮箱>"
git commit -m "提交说明"
```

### `nothing to commit, working tree clean`

当前没有未提交的改动，无需再次提交。

### `Updates were rejected because the remote contains work that you do not have locally`

远端有你本地没有的提交（常见于先在 GitHub 网页上改过文件）。先拉取再推送：

```powershell
git pull --rebase origin main
git push
```

### `error: failed to push some refs` 且提示文件过大

检查是否误提交了构建产物或第三方二进制。用上一节的检查清单定位，
移除后（必要时重写历史）再推送。GitHub 单个文件上限为 100 MB。
