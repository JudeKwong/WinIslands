<div align="center">

<img src="docs/winislands-logo.png" width="104" alt="WinIslands" />

# WinIslands · Windows 灵动岛

**一抬眼，桌面信息都在岛上。**

把媒体、歌词、通知、系统状态和第三方信息，收进一颗会呼吸的胶囊。

[![最新版本](https://img.shields.io/github/v/release/JudeKwong/WinIslands?color=6C5CE7&label=最新版本)](https://github.com/JudeKwong/WinIslands/releases/latest)
[![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4)](#下载安装)
[![开源许可](https://img.shields.io/badge/License-MIT-3cbf91)](LICENSE)
[![.NET 与 WPF](https://img.shields.io/badge/.NET%208-WPF-8b6ca8)](#从源码构建)

**[下载 Windows 版](https://github.com/JudeKwong/WinIslands/releases/latest)** · [快速上手](#快速上手) · [插件开发](docs/PLUGIN_DEVELOPMENT.md) · [上岛 API](docs/IslandAPI.md) · [更新记录](RELEASE_NOTES.md) · [反馈问题](https://github.com/JudeKwong/WinIslands/issues)

</div>

WinIslands 是一款开源的 Windows 灵动岛组件。平时收起在屏幕顶部，鼠标靠近就展开；切歌、看歌词、处理通知、查看系统状态，都能在抬手之间完成。

它使用 **.NET 8 + WPF** 原生构建，不依赖 Electron，支持 Windows 10 / 11 与 x64 / ARM64。界面、组件和交互都可以按自己的习惯调整。


## 听歌时，歌词就在抬眼处

播放、暂停、切歌、调节音量和拖动进度，都能直接在岛上操作。展开时看完整歌词，收起后只留一行，让桌面继续做自己的事。


- **主流播放器兼容**：通过 Windows 全局媒体会话（SMTC）接入网易云音乐、QQ 音乐、Spotify、Apple Music 等播放器，并额外支持 Cider 本地 API。
- **逐字歌词与双语显示**：本地 LRC、AMLL TTML、播放器歌词接口和可选在线歌词统一管理，支持逐字高亮、翻译、一句复制和逐首时间微调。
- **歌词源随手切换**：在自动、本地 LRC、AMLL TTML、Cider API 和在线歌词之间循环切换，立即重新加载当前歌曲。
- **独立歌词窗口**：需要把歌词放到别处时，可以单独打开歌词小窗。

## 消息来了，看一眼岛边

普通通知、微信和 QQ 提醒会以统一的胶囊样式出现。红点、应用图标、标题和摘要重新对齐，紧凑态与展开态使用同一套交互。

- **通知卡片重新设计**：统一为双行胶囊布局，长文本自动省略，图片、进度、按钮和输入框等富内容按需展开。
- **操作按钮统一在下方**：「查看」和「关闭」使用一致的尺寸、悬停底色和按压缩放反馈。
- **微信 / QQ 快速跳转**：点「查看」切回对应窗口，关闭后继续展示队列中的下一条。
- **通知动效**：普通通知带矢量铃铛衰减摆动，复制完成等操作提供轻量反馈。

## 把常用信息摆在手边

时间、天气、日期、CPU / GPU / 内存 / 磁盘、网络速度、电量、输入法和快捷开关等 30+ 组件，都可以自由组合。

| 想看的 | WinIslands 能做什么 |
| --- | --- |
| 今天的时间和天气 | 时间、日期、农历、节气与天气组件，单行或多行排列 |
| 电脑现在怎么样 | CPU、GPU、内存、磁盘、网络、电量和音频波纹状态 |
| 常用操作快一点 | 快捷开关、快速启动器、剪贴板历史、番茄钟和待办 |
| 只看自己需要的 | 组件显隐、图标、排序、播放 / 空闲状态分别配置 |

组件支持拖拽排序、单行 / 多行模式、自定义图标和字体缩放。完整布局可以在 **设置 → 组件** 中调整。

## 插件，把岛变成自己的

WinIslands 支持本地组件插件。插件以独立进程运行，读取标准输出后更新组件，不会加载进主进程；插件崩溃、超时或返回错误 JSON 也不会拖垮应用。

- 支持 `.exe`、`.cmd`、`.bat`、`.ps1` 和 `.com` 入口。
- 可设置启动运行、定时刷新、超时和最大输出大小。
- 用户插件目录与便携插件目录同时扫描，同名插件以用户目录优先。
- **设置 → 插件** 可以查看已加载插件、打开插件目录、重新扫描，并直接打开开发指南。

插件结构与输出协议见 [插件开发指南](docs/PLUGIN_DEVELOPMENT.md)。

## 任何应用，都能把信息送上岛

通过本地 HTTP / WebSocket 接口，第三方软件可以把下载进度、任务状态、播放通知或其他实时信息推送到灵动岛。

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| `POST` | `/v1/island/push` | 推送或更新一张卡片 |
| `POST` | `/v3/island/push` | 支持图片、动态进度和心跳续期 |
| `PATCH` | `/v3/island/push/{id}` | 部分更新卡片 |
| `DELETE` | `/v1/island/push/{id}` | 移除卡片 |
| `GET` | `/v1/island/active` | 查询当前活跃卡片 |
| `GET` | `/v3/ws` | WebSocket 双向通道 |
| `GET` | `/v1/health` | 健康检查 |

支持标题、正文、图标、副标题、进度、按钮、输入框、图片、主题、强调色和优先级队列。推送不会改变灵动岛宽度。

完整接口与示例见 [上岛 API 文档](docs/IslandAPI.md) 和 [SDK 示例](docs/sdk-examples)。

## 展开是面板，收起是状态

- **悬停展开**：鼠标靠近自动展开，移开后收回，无需额外点击。
- **滚轮切换页面**：展开模式下用鼠标滚轮横向切换，动画保持连续。
- **尺寸统一**：展开状态使用固定尺寸，避免不同页面之间跳动或显示不全。
- **控制区固定在下方**：播放、切歌、音量和页面切换放在统一位置。
- **多主题与高 DPI**：内置多套主题、自定义强调色和壁纸取色，适配 PerMonitorV2 高 DPI。
- **贴近桌面习惯**：支持边缘吸附、全屏 / 锁屏自动隐藏、开机自启、勿扰模式和全局快捷键。

## 下载安装

**[前往 GitHub Releases 下载最新版](https://github.com/JudeKwong/WinIslands/releases/latest)**

| 下载文件 | 使用方式 |
| --- | --- |
| `WinIslands-Setup-版本号.exe` | **推荐**。通用安装包，自动按 x64 / ARM64 安装，可选择桌面快捷方式和开机自启 |
| `WinIslands-版本号-win-x64.exe` | x64 便携版，单文件免安装，适合主流 64 位电脑 |
| `WinIslands-版本号-win-arm64.exe` | ARM64 便携版，适合 Surface Pro X、骁龙 PC 等设备 |

**系统要求**：Windows 10 1809+ / Windows 11，x64 或 ARM64。发布包已包含 .NET 8 运行时，无需另外安装。

**升级与卸载**：直接从新版本安装包覆盖安装即可保留设置和数据；卸载默认保留 `%APPDATA%\WinIslands` 中的个人配置。

当前程序未做商业代码签名，Windows 可能提示“未知发布者”。请从本仓库 Releases 下载，并用 Release 页面提供的 SHA256 校验文件。

<details>
<summary id="快速上手"><b>快速上手</b></summary>

1. 下载并运行安装包或便携版，托盘出现 WinIslands 图标。
2. 播放任意音乐，灵动岛会通过系统媒体会话显示播放信息。
3. 鼠标移到岛上展开面板，移开后自动收回。
4. 打开 **设置**，选择主题、组件、插件、通知和开机自启。
5. 关闭主窗口只会隐藏到托盘；需要完全退出时使用托盘菜单中的「退出」。

常用快捷键：

| 快捷键 | 功能 |
| --- | --- |
| `Ctrl+Alt+P` | 播放 / 暂停 |
| `Ctrl+Alt+←` / `Ctrl+Alt+→` | 上一首 / 下一首 |
| `Ctrl+Alt+I` | 显示 / 隐藏灵动岛 |
| `Ctrl+Alt+Space` | 展开 / 收起 |
| `Ctrl+Space` | 快速启动器 |
| `Ctrl+Alt+V` | 剪贴板历史 |

常用命令行参数：`--demo` 演示模式 · `--diagnose` 输出诊断报告 · `--settings` 启动时打开设置。

</details>

<details>
<summary><b>插件开发</b></summary>

插件放在以下任一目录：

```text
%APPDATA%\WinIslands\plugins\<plugin-id>\plugin.json
<WinIslands.exe 所在目录>\plugins\<plugin-id>\plugin.json
```

最小结构：

```text
hello-world/
├─ plugin.json
└─ run.ps1
```

插件向标准输出返回 UTF-8 JSON，例如：

```json
{
  "components": [
    {
      "id": "hello",
      "text": "本地组件 · 08:53:10",
      "icon": "🧩",
      "tooltip": "这是一个由本地插件贡献的 WinIslands 组件。",
      "show_when_playing": true,
      "show_when_idle": true,
      "order": 50
    }
  ]
}
```

字段、刷新策略、示例脚本和错误处理见 [插件开发指南](docs/PLUGIN_DEVELOPMENT.md)。

</details>

<details>
<summary><b>上岛 API 示例</b></summary>

默认基址为 `http://127.0.0.1:9840`，端口可在设置中修改。

```powershell
$body = @{
    id = "download-1"
    title = "正在下载"
    body = "WinIslands-3.1.5-win-x64.exe"
    progress = 0.65
    type = "info"
} | ConvertTo-Json

Invoke-RestMethod -Method Post `
    -Uri "http://127.0.0.1:9840/v1/island/push" `
    -ContentType "application/json" `
    -Body $body
```

按钮、图片、动态进度、心跳续期、WebSocket 和输入框用法见 [上岛 API 文档](docs/IslandAPI.md)。

</details>

<details>
<summary><b>配置、数据与隐私</b></summary>

- 配置文件位于 `%APPDATA%\WinIslands\settings.json`，设置界面的改动会即时生效。
- 插件位于 `%APPDATA%\WinIslands\plugins`。
- 无遥测、无广告、无数据上报。除用户主动开启的在线歌词和天气外，应用保持离线。
- 唯一联网场景包括 Cider 封面、AMLL 逐字歌词、在线歌词和天气查询。
- 所有配置、日志与用户数据只保存在本机。

</details>

<details>
<summary id="从源码构建"><b>从源码构建与参与开发</b></summary>

需要 Windows 10 1809+ / Windows 11 和 .NET 8 SDK。

```powershell
dotnet build src\WinIslands\WinIslands.csproj -c Release
dotnet test tests\WinIslands.Tests\WinIslands.Tests.csproj -c Release
```

发布自包含版本：

```powershell
.\build\publish.ps1
```

框架依赖版本：

```powershell
.\build\publish.ps1 -FrameworkDependent
```

生成安装包需要 [Inno Setup 6](https://jrsoftware.org/isinfo.php)：

```powershell
iscc.exe build\release-<版本>.iss
```

关键目录：

```text
src\WinIslands\             应用主体
tests\WinIslands.Tests\     测试
docs\IslandAPI.md           上岛 API 文档
docs\PLUGIN_DEVELOPMENT.md  插件开发指南
build\                      发布与安装脚本
```

</details>

## 开源与致谢

WinIslands 原创代码采用 [MIT License](LICENSE)。.NET、WPF、Windows SDK 以及开发测试依赖的许可说明见 [THIRD_PARTY.md](THIRD_PARTY.md)。

感谢所有使用、反馈和参与改进 WinIslands 的朋友。

## 感谢支持

如果 WinIslands 让你的桌面顺手了一点，欢迎点一个 **Star**，也欢迎通过 [Issues](https://github.com/JudeKwong/WinIslands/issues) 反馈问题和建议。

<div align="center">

**[下载 WinIslands](https://github.com/JudeKwong/WinIslands/releases/latest)** · [官网](https://WinIslands.JudeKwong.com) · [更新记录](RELEASE_NOTES.md)

</div>