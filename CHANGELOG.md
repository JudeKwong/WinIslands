# WinIslands 更新日志

本页保留最近一个版本的完整说明；历史版本按次要版本归档，见文末索引。

---

## WinIslands 3.1.8（正式版 / Stable）
### 更新内容

- 🔐 **插件安全体系**：插件入口文件必须通过 SHA-256 校验后才会运行；新增「校验并信任」流程、权限白名单与工作目录逃逸防护，导入前会明确展示插件申请的权限；插件进程纳入系统作业对象沙箱，主程序退出时自动结束，单个插件内存上限 1 GB，未声明 processes 权限时禁止派生子进程
- ♿ **可访问性**：自动为按钮、输入框、滑块等控件补齐屏幕阅读器名称和帮助文本；新通知与复制成功提示可通过 UI Automation 播报；高对比度模式下关闭半透明光学层并改用系统颜色，内容始终保持清晰可读
- 🔄 **更新流程加固**：下载完成后对落盘文件再次校验 SHA-256 与文件大小，校验失败立即删除临时文件；设置页可直接查看下载进度与校验结果，并一键重启完成安装
- 🧪 **稳定性验证**：新增连续展开/收起 2000 次压力测试、动画令牌失效测试与更新包校验测试，旧的动画回调不会再改写最终状态
- ⚙️ **构建与发布**：升级到 .NET 10；新增 Windows x64 / ARM64 与 macOS Intel / Apple Silicon 自动化构建流水线，包含安装包、签名脚本与发布资产 SHA256 清单
- 📚 **文档整理**：更新日志按版本归档，新增常见问题、故障排查与插件安全指南，旧链接自动指向新的归档位置
- ✅ **质量验证**：Release 编译 0 错误，全部 701 项单元测试通过；Windows x64 / ARM64 便携版与安装包已完成校验

## WinIslands 3.1.8 (Stable)
### What's New

- 🔐 **Plugin security**: plugin entry files now run only after SHA-256 verification. Added a verify-and-trust flow, a permission allow-list, and working-directory escape protection; requested permissions are shown before import. Plugin processes run inside a system job object that ends with the app, caps each plugin at 1 GB of memory, and blocks child processes unless the plugin declares the processes permission
- ♿ **Accessibility**: buttons, text boxes and sliders automatically receive screen-reader names and help text; new notices and copy confirmations can be announced through UI Automation; high-contrast mode disables translucent optical layers and switches to system colors for readability
- 🔄 **Hardened updates**: the downloaded file is re-verified against its SHA-256 hash and size, and the temporary file is deleted on mismatch. Settings now shows download progress and verification status with a one-click restart to install
- 🧪 **Stability**: added a 2000-iteration expand/collapse stress test, animation-token invalidation tests and update-package verification tests, so stale animation callbacks can no longer overwrite the final state
- ⚙️ **Build and release**: upgraded to .NET 10; added automated pipelines for Windows x64 / ARM64 and macOS Intel / Apple Silicon, including an installer, signing scripts and a SHA256 manifest for release assets
- 📚 **Documentation**: release notes are archived per version, with new FAQ, troubleshooting and plugin-security guides; old links now point to the archive
- ✅ **Verification**: Release build with 0 errors and all 701 unit tests passing; Windows x64 / ARM64 portable builds and the installer were verified

### 校验

发布资产附带 `SHA256SUMS.txt`，可用 `sha256sum -c SHA256SUMS.txt`（macOS/Linux）或 `Get-FileHash`（Windows）核对下载文件完整性。

### Checksums

Every release asset ships with `SHA256SUMS.txt`; verify downloads with `sha256sum -c SHA256SUMS.txt` (macOS/Linux) or `Get-FileHash` (Windows).

## WinIslands 3.1.7（正式版 / Stable）
### 更新内容

- 🧊 **界面质感重新设计**：设置、右键菜单、托盘菜单、首次引导与媒体源检查页统一为真实桌面采样的高斯毛玻璃；灵动岛与展开态保持经典纯黑，不引入液态玻璃
- 🖼️ **毛玻璃背景修复**：新增 Windows 11 主题壁纸回退读取（TranscodedWallpaper 与缓存缩略图），注册表壁纸路径为空或系统接口拒绝访问时，仍能显示真实模糊背景
- 🚪 **首次启动引导**：新增三步引导，依次确认媒体来源、在线歌词与通知权限，并可直接检查当前播放器连接；老用户升级不会重复弹出
- 🔎 **设置页增强**：新增设置搜索、恢复默认设置、创建示例插件与插件依赖提示，查找和调试更直接
- 🔔 **通知合并与免打扰**：同一应用的连续通知合并显示并标注 ×N 次数；开启免打扰后不再弹出通知，也不会残留圆形应用图标
- 🧭 **展开/收起稳定性**：新增展开动画状态机，旧动画的延迟回调会被丢弃，连续展开、收起不再出现方形直角、尺寸错位或画面闪回
- ⚡ **后台活动节流**：锁屏、全屏、省电模式或隐藏灵动岛时自动暂停或降频后台轮询，降低常驻资源占用
- 🎵 **歌词与媒体页细节**：歌词翻译/复制按钮改为紧凑样式；展开音乐页进度条独立占行，时间文字与封面、歌名、控制按钮不再重叠
- 🛠️ **代码结构整理**：窗口与视图模型按媒体、歌词、动画、通知等职责拆分，便于后续维护；新增引导、后台节流、展开状态机等单元测试
- ✅ **质量验证**：Release 编译 0 错误，682 项单元测试全部通过；Windows x64 / ARM64 便携版与通用安装包、macOS Intel / Apple Silicon 版本均已构建

## WinIslands 3.1.7 (Stable)
### What's New

- 🧊 **Refined interface material**: settings, context menu, tray menu, first-run onboarding and the media-source check now share true desktop-sampled Gaussian frosted glass. The island and expanded layout remain classic solid black with no liquid-glass effect
- 🖼️ **Frosted-glass background fix**: added a Windows 11 wallpaper fallback (TranscodedWallpaper and cached thumbnails), so a real blurred backdrop still appears when the registry wallpaper path is empty or the system API denies access
- 🚪 **First-run onboarding**: a three-step guide confirms media sources, online lyrics and notification access, and can check the current player connection. Existing users are not prompted again after upgrading
- 🔎 **Settings improvements**: added settings search, restore-defaults, sample-plugin creation and plugin dependency hints
- 🔔 **Notification grouping and Do Not Disturb**: consecutive notifications from the same app merge into one card with an ×N counter; Do Not Disturb suppresses notifications and no longer leaves a circular app icon behind
- 🧭 **Stable expand/collapse**: a new motion state machine discards late callbacks from stale animations, so repeated expand/collapse no longer produces square corners, wrong sizes or visual flashbacks
- ⚡ **Background activity throttling**: background polling pauses or slows down while the session is locked, a full-screen app is active, battery saver is on, or the island is hidden
- 🎵 **Lyrics and media polish**: compact translate/copy buttons for lyrics; the expanded music progress bar has its own row, with time labels no longer overlapping artwork, title or controls
- 🛠️ **Code organization**: windows and view models are split by responsibility (media, lyrics, animation, notifications), with new unit tests for onboarding, background gating and the motion state machine
- ✅ **Verification**: Release build completed with 0 errors and all 682 unit tests passed. Windows x64 / ARM64 portable builds plus the universal installer, and macOS Intel / Apple Silicon builds, were all produced successfully

### SHA256

| File | SHA256 |
| --- | --- |
| WinIslands-3.1.7-win-x64.exe | 6c147f1dce400afb50c158af5b3c64293cac8e21a8ae3fb2676d72323ea0f98e |
| WinIslands-3.1.7-win-arm64.exe | 507845700fb891558efca1be33dbeae19f5cb0ed7993010f7d0639ce4db705d1 |
| WinIslands-Setup-3.1.7.exe | 45f9b3d687eaeee153f0506611b756a7184fd32035e6799461eff1e018fd4b9a |
| WinIslands-3.1.7-osx-x64.zip | 3a17a045dd54b210c6c26a461ad511c75ec30d96c6def0108f146ce2ff9836ff |
| WinIslands-3.1.7-osx-arm64.zip | 8cfa81a87d645e763dee4e869451832d578abac88ada1270181f92ee6c2b66ca |
---

## 历史版本归档

| 系列 | 归档文件 |
| --- | --- |
| 1.0.x | [docs/release-notes/1.0.md](docs/release-notes/1.0.md) |
| 1.1.x | [docs/release-notes/1.1.md](docs/release-notes/1.1.md) |
| 1.2.x | [docs/release-notes/1.2.md](docs/release-notes/1.2.md) |
| 1.3.x | [docs/release-notes/1.3.md](docs/release-notes/1.3.md) |
| 1.4.x | [docs/release-notes/1.4.md](docs/release-notes/1.4.md) |
| 1.5.x | [docs/release-notes/1.5.md](docs/release-notes/1.5.md) |
| 1.6.x | [docs/release-notes/1.6.md](docs/release-notes/1.6.md) |
| 1.7.x | [docs/release-notes/1.7.md](docs/release-notes/1.7.md) |
| 1.8.x | [docs/release-notes/1.8.md](docs/release-notes/1.8.md) |
| 1.9.x | [docs/release-notes/1.9.md](docs/release-notes/1.9.md) |
| 2.0.x | [docs/release-notes/2.0.md](docs/release-notes/2.0.md) |
| 2.1.x | [docs/release-notes/2.1.md](docs/release-notes/2.1.md) |
| 2.2.x | [docs/release-notes/2.2.md](docs/release-notes/2.2.md) |
| 2.3.x | [docs/release-notes/2.3.md](docs/release-notes/2.3.md) |
| 2.4.x | [docs/release-notes/2.4.md](docs/release-notes/2.4.md) |
| 2.5.x | [docs/release-notes/2.5.md](docs/release-notes/2.5.md) |
| 2.6.x | [docs/release-notes/2.6.md](docs/release-notes/2.6.md) |
| 2.7.x | [docs/release-notes/2.7.md](docs/release-notes/2.7.md) |
| 2.8.x | [docs/release-notes/2.8.md](docs/release-notes/2.8.md) |
| 2.9.x | [docs/release-notes/2.9.md](docs/release-notes/2.9.md) |
| 3.1.x | [docs/release-notes/3.1.md](docs/release-notes/3.1.md) |
