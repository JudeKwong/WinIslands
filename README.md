<div align="center">

**选择语言 / Select Language**

[简体中文](#简体中文) · [English](#english)

</div>

---

## 简体中文

# WinIslands — Windows 灵动岛

> **把 iOS 的灵动岛带到 Windows 11 / 10 —— 一款现代化、多功能的 Windows 灵动岛组件。**
> 基于 **.NET 8 + WPF**，免费开源（MIT），**无广告 · 无遥测 · 不上报数据**。

🌐 官网：https://WinIslands.JudeKwong.com ｜ GitHub：https://github.com/JudeKwong/WinIslands

---

## ✨ 功能亮点

- **▶ 媒体播放控制**：原生接入 Windows 全局媒体会话（SMTC），兼容网易云、QQ音乐、Spotify、Apple Music、Groove、电影和电视等主流播放器；额外专门支持 Cider 本地 API；无法接入时窗口标题兜底。支持专辑封面、进度拖拽 seek、播放/暂停/切歌、音量调节；多播放器同时打开时可一键切换控制来源。
- **♪ 卡拉OK逐字歌词**：展开卡片同步滚动高亮、逐字点亮；歌词来源优先级：本地 `.lrc` → AMLL TTML → 播放器歌词接口 → 可选在线歌词，并可一键循环切换来源；支持双语歌词、翻译开关、一句复制、逐首时间微调与独立歌词小窗。
- **▦ 可定制组件系统**：时间、天气、日期（含农历/节气）、CPU/GPU/内存/磁盘、网络速度、电量、输入法、快捷开关等 30+ 组件；每个组件可自定义图标、勾选显隐与拖拽排序，支持单行/多行模式；还支持本地组件插件（`.exe`/`.cmd`/`.bat`/`.ps1`，独立进程运行）。
- **🏝 上岛 API**：本地 HTTP / WebSocket 接口，让任何第三方软件把信息实时推送到灵动岛（类似 iOS 第三方 App 的灵动岛集成）。支持图片、动态进度、心跳续期、深浅主题、按钮动作、输入框；推送不影响灵动岛长宽，也不遮挡其他组件。
- **✨ 提示动画**：蓝牙连接/断开、开始/结束充电、低电量、网络恢复、日历/RSS/邮件提醒等事件，以 iOS 风格动画在灵动岛上展示。
- **✦ 外观与动效**：18 种主题皮肤、自定义强调色、液态玻璃毛玻璃、壁纸取色、跑马灯、4 种动效皮肤（iOS 弹簧等）、4 种音频波纹样式（随音乐节奏抖动）；展开/收起非线性缓动，60fps 丝滑；PerMonitorV2 高 DPI 适配。
- **🖱 交互与智能**：解锁拖动 + 边缘吸附、全屏/锁屏自动隐藏、双击/中键快捷动作、快捷操作按钮、文件中转站（拖文件上岛、可再拖出到其他应用）、录制智能勿扰、定时明暗主题切换。
- **⚡ 效率工具与自动化**：番茄钟、待办、剪贴板历史、快速启动器、日程提醒；会议静音助手、屏幕录制/截图提示、文件复制/下载进度上岛；全局快捷键与规则引擎。
- **🛡 隐私安全**：无遥测、无广告、无数据上报。除用户手动开启的在线歌词/天气外完全离线；所有配置与数据仅存于本机 `%APPDATA%\WinIslands`。
- **🔁 多歌词源一键切换**：在「自动 / 本地 LRC / AMLL TTML / Cider API / 在线歌词」之间一键循环切换，立即重新加载当前歌曲歌词。
- **🛡️ 崩溃自动恢复**：异常退出后下次启动自动提示已恢复，不再无响应、黑屏、状态丢失。
- **⏱️ 动画时长可调**：展开/收起动画时长滑杆（300–1400ms），自由调节快慢，适配个人喜好。

---

## 📥 下载（最新稳定版 3.1.1）

| 平台 | 下载 | 说明 |
| --- | --- | --- |
| Windows x64 | [x64 便携版](https://github.com/JudeKwong/WinIslands/releases/download/3.1.1/WinIslands-3.1.1-win-x64.exe) | 主流 64 位电脑首选，单文件免安装，直接运行 |
| Windows ARM64 | [ARM64 便携版](https://github.com/JudeKwong/WinIslands/releases/download/3.1.1/WinIslands-3.1.1-win-arm64.exe) | Surface Pro X / 骁龙机型等 ARM 设备 |
| Windows 通用 | [通用安装包](https://github.com/JudeKwong/WinIslands/releases/download/3.1.1/WinIslands-Setup-3.1.1.exe) | Inno Setup 安装向导，x64 / ARM64 自动按架构安装 |
| macOS (Apple 芯片) | [arm64 版](https://github.com/JudeKwong/WinIslands/releases/download/3.1.1/WinIslands-3.1.1-osx-arm64.zip) | Apple 芯片 Mac（M1 及更新机型） |
| macOS (Intel) | [x64 版](https://github.com/JudeKwong/WinIslands/releases/download/3.1.1/WinIslands-3.1.1-osx-x64.zip) | Intel 芯片 Mac |

> ⚠️ macOS 版为实验性版本：部分功能可能不完整，可能存在较多 BUG，且尚未经过充分测试，请谨慎使用。

历史版本见 [GitHub Releases](https://github.com/JudeKwong/WinIslands/releases)。

---

## 📊 性能指标

| 指标 | 数值 |
| --- | --- |
| 常驻内存（Private） | ~72 MB |
| 冷启动 | < 1 s |
| 空闲 CPU | ≈ 0% |
| 动效帧率 | 60 fps |
| 多实例 | 单实例防重复运行 |
| 遥测 | 0 遥测 · 无上报 · 无广告 |

---

## 🔧 构建

### 环境要求
- Windows 10 1809+ / Windows 11
- .NET 8 SDK

### 构建与测试
```powershell
dotnet build WinIslands.slnx -c Release
dotnet test  WinIslands.slnx -c Release
```

### 发布
```powershell
# 自包含（含 .NET 8 运行时，免安装运行）
.\build\publish.ps1

# 框架依赖（体积小，需安装 .NET 8 Desktop Runtime）
.\build\publish.ps1 -FrameworkDependent
```
产物位于 `publish\win-x64\`。正式版按版本放到 `releases\<版本>\win-x64\` 并按版本号重命名。

### 安装包（可选）
安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php) 后执行 `iscc.exe build\release-<版本>.iss`，生成 `releases\<版本>\WinIslands-Setup-<版本>.exe`（通用安装包，x64 / ARM64 自动按架构安装）。

---

## 🚀 使用说明

1. 启动 `WinIslands.exe`（可设置开机自启），托盘出现图标；关闭主窗口不会退出进程，仅托盘化。
2. 播放任意音乐：网易云、QQ音乐、Spotify、Apple Music 官方版等自动通过系统媒体会话显示；Cider 详见下文；其它播放器兜底窗口标题识别。
3. **点击**灵动岛展开完整卡片（悬停不展开）：进度拖拽 seek、播放控制、音量、同步歌词；再点一下收回。
4. 托盘菜单：显示/隐藏、独立歌词窗口、开机自启、勿扰模式、检查更新、查看日志、设置、退出。
5. 全局快捷键（均可自定义）：`Ctrl+Alt+P` 播放/暂停 · `Ctrl+Alt+←/→` 上一首/下一首 · `Ctrl+Alt+I` 显示/隐藏 · `Ctrl+Alt+Space` 展开/收起 · `Ctrl+Space` 快速启动器 · `Ctrl+Alt+V` 剪贴板历史面板。
6. 常用命令行参数：`--demo` 演示模式 · `--diagnose` 输出诊断报告 · `--settings` 启动时打开设置。

### Cider 集成
1. 在 Cider 设置中开启「允许外部控制」（Allow external control）。
2. WinIslands 设置 → 媒体 → 启用 Cider（端口默认自动检测 `10767` 并扫描本机，也可手动填写）。
3. 播放时灵动岛来源显示 `Cider`，可显示封面/进度/歌词并控制播放、seek、音量。

---

## ⚙️ 配置项说明

配置文件：`%APPDATA%\WinIslands\settings.json`（JSON；设置界面改动即时生效，可导出/导入）。

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `Language` | `zh-CN` | 界面语言：`zh-CN` / `en-US` |
| `Position` | `Center` | 位置：`Center` 顶部居中 / `Right` 顶部右侧 |
| `Monitor` | `Primary` | 显示器：`Primary` 主屏 / `All` 所有屏 / `Index` 指定屏 |
| `MonitorIndex` | `0` | 指定屏幕编号 |
| `OffsetX` / `OffsetY` | `0` / `8` | 位置偏移（像素） |
| `Opacity` | `0.92` | 不透明度 0.3–1.0 |
| `Theme` | `Auto` | `Auto` / `Light` / `Dark` |
| `ThemePreset` | `Default` | 主题皮肤（18 种预设 + Custom） |
| `AccentColor` | `#6C5CE7` | 主题色 `#RRGGBB` |
| `FontScale` / `CornerRadius` | `1.0` / `28` | 字号缩放 0.8–1.4 / 圆角 16–40 |
| `AnimationStyle` | `Spring` | 动效皮肤：`Spring` / `Soft` / `Elastic` / `Fade` |
| `CompactWidth` / `CompactHeight` | `360` / `72` | 紧凑长度 / 宽度 |
| `CompactWidthAuto` / `CompactHeightAuto` | `true` | 紧凑尺寸自动调整 |
| `ExpandedWidth` / `MaxExpandedHeight` | `400` / `384` | 展开长度 / 展开最大高度 |
| `HideWhenNoMedia` | `true` | 无媒体播放时隐藏灵动岛 |
| `IslandAlwaysVisible` | `false` | 灵动岛常驻（无媒体时也显示组件） |
| `ShowWhenPaused` | `true` | 暂停时仍显示 |
| `StartWithWindows` | `false` | 开机自启 |
| `IsLocked` | `true` | 上锁（解锁后鼠标可拖动） |
| `EdgeSnapEnabled` | `true` | 拖动松手自动吸附屏幕边缘/居中 |
| `FullScreenAutoHideEnabled` | `true` | 全屏时自动隐藏 |
| `LockScreenAutoHideEnabled` | `true` | 锁屏（Win+L / 远程桌面断开）时自动隐藏，解锁后恢复 |
| `SingleLineMode` | `true` | 单行模式：紧凑态所有组件一行显示 |
| `DoubleClickAction` | `PlayPause` | 双击灵动岛快捷动作 |
| `MiddleClickAction` | `PlayPause` | 中键单击灵动岛快捷动作 |
| `CiderEnabled` / `CiderPort` | `true` / `0` | Cider 本地 API（0=自动检测） |
| `OnlineLyricsEnabled` | `false` | 在线歌词开关（非官方接口，注意版权） |
| `AmllTtmlEnabled` | `true` | AMLL 逐字歌词（api.amll.dev，非官方） |
| `KaraokeHighlight` | `true` | 逐字卡拉OK高亮 |
| `StandaloneLyricsWindow` | `false` | 独立歌词小窗 |
| `UseSystemVolume` | `true` | 非 Cider 来源用系统音量 |
| `LowBatteryThreshold` | `20` | 低电量提醒阈值（%），0 关闭 |
| `IslandApiEnabled` / `IslandApiPort` | `true` / `9840` | 上岛 API 开关 / 端口 |
| `IslandApiToken` | `""` | 上岛 API 可选 Token |
| `IslandApiDefaultDuration` | `30` | 上岛默认显示时长（秒） |
| `WaveStyle` | `Bars` | 音频波纹样式：`Bars` / `Spectrum` / `Ring` / `Particles` |
| `WidgetOrder` | `Time,Weather,...` | 组件摆放顺序（逗号分隔键名） |
| `Components` | 对象 | 各组件「无歌曲 / 有歌曲」两列勾选 |
| `MediaApps` | `[]` | 媒体程序启用/禁用与优先级 |
| `Rules` | `[]` | 自动化规则（条件 + 动作） |
| `ReduceMotion` | `false` | 减少动态效果（无障碍/省电） |
| `GlobalHotkeysEnabled` | `true` | 全局快捷键开关 |
| `LowPowerMode` | `false` | 低功耗模式：空闲降帧、简化动画 |

---

## 🏝 上岛 API（第三方集成）

任何软件都可通过本地 HTTP / WebSocket 接口把信息推送到灵动岛，类似 iOS 第三方 App 的“上岛”。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/v1/island/push` | 推送/更新一张卡片 |
| POST | `/v3/island/push` | v1 超集：图片 / 动态进度 / 心跳 |
| PATCH | `/v3/island/push/{id}` | 部分更新 |
| DELETE | `/v1/island/push/{id}` | 移除卡片 |
| GET | `/v1/island/active` | 查询当前活跃卡片 |
| GET | `/v3/ws` | WebSocket 双向通道 |
| GET | `/v1/health` | 健康检查 |

支持：标题/正文/图标/副标题、进度、按钮（打开链接 / 启动程序 / 执行命令 / notify 回调）、输入框、图片、动态进度、心跳续期、深浅主题、自定义强调色、优先级队列。**推送不会改变灵动岛宽度**。

完整文档：[docs/IslandAPI.md](docs/IslandAPI.md)

---

## 🛡 隐私与安全

- **无遥测、无广告、无上报**。除用户手动开启的“在线歌词”“天气”外，应用不进行任何网络请求。
- 唯一联网场景：Cider 封面下载、AMLL 逐字歌词（api.amll.dev）、用户开启后的在线歌词与天气（Open-Meteo）。
- 所有数据本地存储于 `%APPDATA%\WinIslands\`；日志仅记录本地运行信息。

---

## ⚠️ 已知限制

- 逐字卡拉OK依赖歌词来源与进度：有 AMLL TTML / LRC 逐字时间轴时按字高亮，否则降级为整句高亮。
- 播放器偶发回退进度（如 Cider/SMTC 瞬间上报 0）：已做位置守卫，瞬间回退会被忽略。
- SMTC 覆盖范围取决于播放器是否注册全局媒体会话；个别旧播放器仅能通过窗口标题兜底（无控制按钮）。
- Cider 1.x（旧 API）未适配，仅支持 2.x 及以上。
- 在线歌词 / AMLL / 天气均为非官方接口，可能随版本变动而失效。

---

## ❓ 常见问题

**Q: 灵动岛没有出现？**
确认正在播放；`HideWhenNoMedia` 默认开启，无媒体时隐藏属正常。运行 `--diagnose` 查看会话列表。

**Q: Cider 显示“未连接”？**
确认 Cider 设置中开启“允许外部控制”，检查端口（默认 10767），并在 WinIslands 设置中确认已启用 Cider。

**Q: 退出后托盘图标仍在？**
托盘菜单 → 退出；直接关闭灵动岛窗口仅隐藏（托盘常驻设计）。

---

## 📄 开源许可

- 应用本体：MIT（见 [LICENSE](LICENSE)）
- 第三方组件：见 [THIRD_PARTY.md](THIRD_PARTY.md)

---

## English

# WinIslands — Dynamic Island for Windows

> **Bring the iOS Dynamic Island to Windows 11 / 10 — a modern, multi-functional Dynamic Island widget for Windows.**
> Built with **.NET 8 + WPF**, free and open source (MIT), **no ads · no telemetry · no data collection**.

🌐 Website: https://WinIslands.JudeKwong.com ｜ GitHub: https://github.com/JudeKwong/WinIslands

---

## ✨ Highlights

- **▶ Media playback control**: Native integration with the Windows Global System Media Transport Controls (SMTC) — compatible with NetEase Cloud Music, QQ Music, Spotify, Apple Music, Groove, Movies & TV and more; dedicated support for the Cider local API; window-title fallback when nothing else works. Album artwork, draggable seek, play/pause, previous/next, volume; when several players are open, switch the control source with one click.
- **♪ Karaoke word-by-word lyrics**: The expanded card shows synced scrolling lyrics with character-by-character highlighting. Lyric sources: local `.lrc` → AMLL TTML → player lyric API → optional online lyrics, switchable with one click. Bilingual lyrics, translation toggle, one-click copy of the current line, per-song time fine-tuning and a standalone lyrics window.
- **▦ Customizable widget system**: 30+ widgets — clock, weather, date (Chinese calendar/solar terms), CPU/GPU/RAM/disk, network speed, battery, input method, quick toggles and more. Each widget supports a custom icon, show/hide checkboxes and drag-to-reorder; single-line or multi-line layouts; extensible with local component plugins (`.exe`/`.cmd`/`.bat`/`.ps1` running in separate processes).
- **🏝 Island API**: A local HTTP / WebSocket interface that lets any third-party software push live information to the Dynamic Island (like the Island integration of iOS third-party apps). Supports images, animated progress, heartbeat renewal, light/dark themes, buttons and input fields; pushes never change the island width or cover other widgets.
- **✨ Alert animations**: Bluetooth connect/disconnect, charging start/end, low battery, network recovery, calendar/RSS/mail reminders and more are shown on the island with beautiful iOS-style animations.
- **✦ Appearance & motion**: 18 theme presets, custom accent color, liquid-glass blur, wallpaper color sampling, marquee, 4 motion styles (iOS spring etc.), 4 audio waveform styles (animated with the music); non-linear easing for expand/collapse at 60fps; PerMonitorV2 high-DPI support.
- **🖱 Interaction & smarts**: unlock-and-drag with edge snapping, auto-hide in fullscreen/locked screen, double/middle-click actions, quick action buttons, a file relay (drag files onto the island, then drag them out to other apps), smart DND while recording, scheduled light/dark switching.
- **⚡ Productivity & automation**: Pomodoro timer, to-do list, clipboard history, quick launcher, calendar reminders; mute assistant for meetings, screen recording/screenshot alerts, file copy/download progress on the island; global hotkeys and a rules engine.
- **🛡 Privacy & security**: No telemetry, no ads, no data collection. Fully offline except for user-enabled online lyrics/weather; all config and data stay local in `%APPDATA%\WinIslands`.
- **🔁 One-click lyric source switching**: Cycle through `Auto / Local LRC / AMLL TTML / Cider API / Online lyrics` and instantly reload the current song's lyrics.
- **🛡️ Crash recovery**: After an abnormal exit, the next launch automatically notifies you that the app has recovered — no more freezes, black screens or lost state.
- **⏱️ Adjustable animation duration**: Expand/collapse duration slider (300–1400ms) to match your preference.

---

## 📥 Download (latest stable 3.1.1)

| Platform | Download | Description |
| --- | --- | --- |
| Windows x64 | [x64 portable](https://github.com/JudeKwong/WinIslands/releases/download/3.1.1/WinIslands-3.1.1-win-x64.exe) | For mainstream 64-bit PCs; single file, no install needed |
| Windows ARM64 | [ARM64 portable](https://github.com/JudeKwong/WinIslands/releases/download/3.1.1/WinIslands-3.1.1-win-arm64.exe) | For Surface Pro X / Snapdragon ARM devices |
| Windows Universal | [Universal installer](https://github.com/JudeKwong/WinIslands/releases/download/3.1.1/WinIslands-Setup-3.1.1.exe) | Inno Setup wizard; auto-installs x64 / ARM64 by architecture |
| macOS (Apple Silicon) | [arm64](https://github.com/JudeKwong/WinIslands/releases/download/3.1.1/WinIslands-3.1.1-osx-arm64.zip) | Apple Silicon Macs (M1 and newer) |
| macOS (Intel) | [x64](https://github.com/JudeKwong/WinIslands/releases/download/3.1.1/WinIslands-3.1.1-osx-x64.zip) | Intel Macs |

> ⚠️ macOS build is experimental: some features may be incomplete, it may contain bugs, and it has not been fully tested. Use with caution.

All historical versions: [GitHub Releases](https://github.com/JudeKwong/WinIslands/releases).

---

## 📊 Performance

| Metric | Value |
| --- | --- |
| Resident memory (Private) | ~72 MB |
| Cold start | < 1 s |
| Idle CPU | ≈ 0% |
| Animation frame rate | 60 fps |
| Multiple instances | Single instance, prevents duplicates |
| Telemetry | 0 telemetry · no uploads · no ads |

---

## 🔧 Building

### Requirements
- Windows 10 1809+ / Windows 11
- .NET 8 SDK

### Build & test
```powershell
dotnet build WinIslands.slnx -c Release
dotnet test  WinIslands.slnx -c Release
```

### Publish
```powershell
# Self-contained (includes the .NET 8 runtime, no install needed)
.\build\publish.ps1

# Framework-dependent (small, requires .NET 8 Desktop Runtime)
.\build\publish.ps1 -FrameworkDependent
```
Output goes to `publish\win-x64\`. For stable releases, place the build under `releases\<version>\win-x64\` and rename it with the version number.

### Installer (optional)
Install [Inno Setup 6](https://jrsoftware.org/isinfo.php), then run `iscc.exe build\release-<version>.iss` to produce `releases\<version>\WinIslands-Setup-<version>.exe` (universal installer; auto-installs x64 / ARM64 by architecture).

---

## 🚀 Usage

1. Launch `WinIslands.exe` (startup with Windows is optional); a tray icon appears. Closing the main window does not quit the process — it just minimizes to the tray.
2. Play any music: NetEase Cloud Music, QQ Music, Spotify, Apple Music official app, etc. appear automatically through the system media session; Cider — see below; other players fall back to window-title detection.
3. **Click** the island to expand the full card (hovering does not expand): draggable seek, playback controls, volume, synced lyrics; click again to collapse.
4. Tray menu: show/hide, standalone lyrics window, start with Windows, Do Not Disturb, check for updates, view logs, settings, quit.
5. Global hotkeys (all customizable): `Ctrl+Alt+P` play/pause · `Ctrl+Alt+←/→` previous/next · `Ctrl+Alt+I` show/hide · `Ctrl+Alt+Space` expand/collapse · `Ctrl+Space` quick launcher · `Ctrl+Alt+V` clipboard history panel.
6. Useful command-line arguments: `--demo` demo mode · `--diagnose` writes a diagnostic report · `--settings` opens settings at startup.

### Cider integration
1. Enable "Allow external control" in Cider settings.
2. WinIslands Settings → Media → enable Cider (port auto-detected from `10767` with a local scan, or set manually).
3. While playing, the island source shows `Cider`; you can display artwork/progress/lyrics and control play, seek and volume.

---

## ⚙️ Configuration

Config file: `%APPDATA%\WinIslands\settings.json` (JSON; changes in the settings UI apply instantly, export/import supported).

| Key | Default | Description |
| --- | --- | --- |
| `Language` | `zh-CN` | UI language: `zh-CN` / `en-US` |
| `Position` | `Center` | Position: `Center` top-center / `Right` top-right |
| `Monitor` | `Primary` | Monitor: `Primary` / `All` / `Index` |
| `MonitorIndex` | `0` | Specific monitor number |
| `OffsetX` / `OffsetY` | `0` / `8` | Position offset (pixels) |
| `Opacity` | `0.92` | Opacity 0.3–1.0 |
| `Theme` | `Auto` | `Auto` / `Light` / `Dark` |
| `ThemePreset` | `Default` | Theme preset (18 presets + Custom) |
| `AccentColor` | `#6C5CE7` | Accent color `#RRGGBB` |
| `FontScale` / `CornerRadius` | `1.0` / `28` | Font scale 0.8–1.4 / corner radius 16–40 |
| `AnimationStyle` | `Spring` | Motion style: `Spring` / `Soft` / `Elastic` / `Fade` |
| `CompactWidth` / `CompactHeight` | `360` / `72` | Compact width / height |
| `CompactWidthAuto` / `CompactHeightAuto` | `true` | Auto-adjust compact size |
| `ExpandedWidth` / `MaxExpandedHeight` | `400` / `384` | Expanded width / max expanded height |
| `HideWhenNoMedia` | `true` | Hide the island when no media is playing |
| `IslandAlwaysVisible` | `false` | Keep the island visible without media |
| `ShowWhenPaused` | `true` | Keep showing while paused |
| `StartWithWindows` | `false` | Start with Windows |
| `IsLocked` | `true` | Locked (unlock to drag with the mouse) |
| `EdgeSnapEnabled` | `true` | Snap to screen edge/center when released |
| `FullScreenAutoHideEnabled` | `true` | Auto-hide in fullscreen |
| `LockScreenAutoHideEnabled` | `true` | Auto-hide on lock screen (Win+L / RDP disconnect), restore on unlock |
| `SingleLineMode` | `true` | Single-line mode: all widgets in one row when collapsed |
| `DoubleClickAction` | `PlayPause` | Double-click quick action |
| `MiddleClickAction` | `PlayPause` | Middle-click quick action |
| `CiderEnabled` / `CiderPort` | `true` / `0` | Cider local API (0 = auto-detect) |
| `OnlineLyricsEnabled` | `false` | Online lyrics switch (unofficial interface, mind copyright) |
| `AmllTtmlEnabled` | `true` | AMLL word-by-word lyrics (api.amll.dev, unofficial) |
| `KaraokeHighlight` | `true` | Word-by-word karaoke highlight |
| `StandaloneLyricsWindow` | `false` | Standalone lyrics window |
| `UseSystemVolume` | `true` | Use system volume for non-Cider sources |
| `LowBatteryThreshold` | `20` | Low-battery reminder threshold (%), 0 = disabled |
| `IslandApiEnabled` / `IslandApiPort` | `true` / `9840` | Island API switch / port |
| `IslandApiToken` | `""` | Optional Island API token |
| `IslandApiDefaultDuration` | `30` | Default push duration (seconds) |
| `WaveStyle` | `Bars` | Audio waveform style: `Bars` / `Spectrum` / `Ring` / `Particles` |
| `WidgetOrder` | `Time,Weather,...` | Widget order (comma-separated keys) |
| `Components` | object | Per-widget "no song / playing" two-column checkboxes |
| `MediaApps` | `[]` | Media app enable/disable and priority |
| `Rules` | `[]` | Automation rules (conditions + actions) |
| `ReduceMotion` | `false` | Reduce motion (accessibility/battery) |
| `GlobalHotkeysEnabled` | `true` | Global hotkeys switch |
| `LowPowerMode` | `false` | Low-power mode: lower frame rate when idle, simplified animations |

---

## 🏝 Island API (third-party integration)

Any software can push information to the Dynamic Island through the local HTTP / WebSocket interface, similar to iOS third-party App "Island" integration.

| Method | Path | Description |
| --- | --- | --- |
| POST | `/v1/island/push` | Push/update a card |
| POST | `/v3/island/push` | v1 superset: image / animated progress / heartbeat |
| PATCH | `/v3/island/push/{id}` | Partial update |
| DELETE | `/v1/island/push/{id}` | Remove a card |
| GET | `/v1/island/active` | Query the active card |
| GET | `/v3/ws` | WebSocket two-way channel |
| GET | `/v1/health` | Health check |

Supports: title/body/icon/subtitle, progress, buttons (open link / launch app / run command / notify callback), input fields, images, animated progress, heartbeat renewal, light/dark themes, custom accent color and a priority queue. **Pushes never change the island's width.**

Full documentation: [docs/IslandAPI.md](docs/IslandAPI.md)

---

## 🛡 Privacy & security

- **No telemetry, no ads, no uploads**. The app makes no network requests except user-enabled "online lyrics" and "weather".
- The only networked scenarios: Cider artwork download, AMLL word-by-word lyrics (api.amll.dev), user-enabled online lyrics and weather (Open-Meteo).
- All data is stored locally in `%APPDATA%\WinIslands\`; logs only record local runtime information.

---

## ⚠️ Known limitations

- Word-by-word karaoke depends on the lyrics source and progress; it highlights per character when AMLL TTML / LRC timing exists, otherwise falls back to whole-line highlighting.
- Players occasionally report regressing progress (e.g. Cider/SMTC momentarily reporting 0): a position guard ignores momentary regressions.
- SMTC coverage depends on whether the player registers a global media session; some old players can only be detected via window title (no control buttons).
- Cider 1.x (old API) is not supported; only 2.x and above.
- Online lyrics / AMLL / weather are unofficial interfaces and may break as versions change.

---

## ❓ FAQ

**Q: The island does not appear?**
Make sure media is playing; `HideWhenNoMedia` is enabled by default, so hiding without media is expected. Run `--diagnose` to see the session list.

**Q: Cider shows "Not connected"?**
Make sure "Allow external control" is enabled in Cider settings, check the port (default 10767), and confirm Cider is enabled in WinIslands settings.

**Q: The tray icon is still there after quitting?**
Use Tray menu → Quit; closing the island window only hides it (tray-resident by design).

---

## 📄 License

- Application: MIT (see [LICENSE](LICENSE))
- Third-party components: see [THIRD_PARTY.md](THIRD_PARTY.md)