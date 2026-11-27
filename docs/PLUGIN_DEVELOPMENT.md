# WinIslands 本地组件插件开发指南

## 1. 插件定位

WinIslands 插件用于向灵动岛增加**常驻组件**，例如 CPU/内存扩展、Home Assistant 状态、下载统计、股票、服务器状态、自定义时间或本地脚本输出。

插件与「上岛 API」是两套独立能力：

| 能力 | 用途 |
| --- | --- |
| 本地组件插件 | 扩展灵动岛组件，参与紧凑/展开布局、自动宽度、显示顺序和播放/空闲显隐 |
| 上岛 API | 只负责第三方软件把一次性信息推送到灵动岛，例如下载完成、任务状态、播放通知 |

插件不会被加载进 WinIslands 主进程。WinIslands 以独立子进程运行插件，读取标准输出后更新组件；插件崩溃、超时或返回错误 JSON 不会拖垮主程序。

## 2. 插件目录

WinIslands 同时扫描两个目录：

1. 用户目录：`%APPDATA%\WinIslands\plugins\<plugin-id>\plugin.json`
2. 便携目录：`<WinIslands.exe 所在目录>\plugins\<plugin-id>\plugin.json`

推荐开发阶段使用用户目录。发布便携版时也可以把插件放在 exe 同级 `plugins` 目录。

同名 `id` 的插件以用户目录优先。托盘菜单中的「插件目录 / Plugin Folder」可以直接打开用户插件目录并重新扫描插件。

## 3. 最小插件结构

```text
hello-world/
├─ plugin.json
└─ run.ps1
```

`plugin.json`：

```json
{
  "id": "com.example.hello-world",
  "name": "Hello World",
  "version": "1.0.0",
  "description": "Sample local component plugin for WinIslands.",
  "author": "Your Name",
  "entry": "run.ps1",
  "arguments": [],
  "enabled": false,
  "run_at_startup": true,
  "interval_seconds": 0,
  "timeout_seconds": 10,
  "max_output_kb": 64
}
```

字段说明：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `id` | 是 | 稳定唯一 ID，只允许字母、数字、点、横线和下划线 |
| `name` | 否 | 显示名称；留空时使用 `id` |
| `version` | 否 | 插件版本，建议使用语义化版本 |
| `entry` | 是 | 相对插件目录的入口文件，不允许跳出插件目录 |
| `arguments` | 否 | 传给入口程序的参数数组 |
| `working_directory` | 否 | 工作目录，默认是插件目录 |
| `enabled` | 否 | 是否启用，默认 `true` |
| `run_at_startup` | 否 | 启动后是否立即运行一次，默认 `true` |
| `interval_seconds` | 否 | 定时运行间隔；`0` 表示只按需/启动运行 |
| `timeout_seconds` | 否 | 单次运行超时，默认 `10` 秒 |
| `max_output_kb` | 否 | 标准输出最大读取量，默认 `256` KB |
| `permissions` | 否 | 声明式权限：`network` / `filesystem` / `processes` |
| `sha256` | 运行必需 | 入口文件 SHA-256；缺失时需在插件管理器中「校验并信任」 |

支持的入口类型：`.exe`、`.cmd`、`.bat`、`.ps1`、`.com`。

`.ps1` 使用 Windows PowerShell 5.1 执行。脚本中包含中文时，请使用 **UTF-8 with BOM** 保存，避免 Windows PowerShell 将脚本按本地 ANSI 编码读取。

## 4. 组件输出协议

插件通过标准输出返回 UTF-8 JSON。推荐使用包装对象：

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

组件字段：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `id` | 否 | 组件在插件内的稳定 ID；省略时自动生成 `component-1` |
| `text` | 是 | 组件显示文本；为空时忽略该组件 |
| `icon` | 否 | Segoe MDL2 字形或 emoji；默认 `\uE8D6` |
| `tooltip` | 否 | 鼠标悬停提示 |
| `show_when_playing` | 否 | 有媒体播放时是否显示，默认 `true` |
| `show_when_idle` | 否 | 无媒体播放时是否显示，默认 `true` |
| `order` | 否 | 同批插件组件的排序值，越小越靠前 |

也支持直接输出单个组件对象、组件数组，或每行一个组件对象。

每次成功运行返回的 `components` 是该插件当前完整组件快照：第一次返回 `clock`、`battery`，下一次只返回 `clock`，则 `battery` 会被移除。返回 `{"components":[]}` 会清空该插件的全部组件；无标准输出时保留上一次组件，避免临时失败造成闪烁。非 JSON 输出会被忽略并写入日志。

## 5. PowerShell 示例

```powershell
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$time = Get-Date -Format 'HH:mm:ss'
$payload = @{
    components = @(
        @{
            id = "hello"
            text = "本地组件 · $time"
            icon = "🧩"
            tooltip = "这是一个由本地插件贡献的 WinIslands 组件。"
            show_when_playing = $true
            show_when_idle = $true
            order = 50
        }
    )
}

$payload | ConvertTo-Json -Compress -Depth 6
```

示例文件位于 `plugins\components\hello-world`。默认 `enabled=false`。把它改为 `true`，然后从托盘菜单打开插件目录并重新扫描，或重启 WinIslands。

## 6. 定时组件

将 `interval_seconds` 设置为 `5`，插件会每 5 秒运行一次。插件进程不会重叠：上一次仍未结束时，下一次定时触发会跳过。适合本地服务器状态、CPU/内存/磁盘/网络采样、股票、NAS 或 Home Assistant 状态。插件适合以 1 秒或更慢的频率更新文本，动画由 WinIslands 负责。

## 7. 调试

1. 打开托盘菜单 →「插件目录」。
2. 确认目录结构为 `插件目录\<plugin-id>\plugin.json`。
3. 手动执行入口脚本，确认标准输出是合法 JSON。
4. 查看日志目录：`%APPDATA%\WinIslands\logs`。
5. 插件日志前缀为 `Plugin ...`，包含清单错误、超时、stderr 和退出码信息。
6. 修改插件后点击托盘菜单中的「插件目录」，WinIslands 会打开目录并重新扫描启用的插件。

## 8. 安全与性能

- 插件是本地代码，拥有当前用户权限。只安装你信任的插件。
- 入口文件必须通过 SHA-256 校验后才会运行；未校验插件只展示、不执行。
- 插件进程受 Job Object 约束：主程序退出时清理、1 GB 内存上限、未授权时禁止子进程。
- WinIslands 不主动上传插件数据。
- 单次运行超时会终止插件进程。
- 标准输出有大小限制，避免插件无限输出占满内存。
- 插件不会直接访问 WinIslands 内部对象；主程序只解析 JSON 组件快照。
- 如果插件不需要常驻，请设置 `run_at_startup=false` 并使用较大的 `interval_seconds`。

## 9. 版本兼容建议

- `id` 发布后不要修改，否则会被视为新插件。
- 组件 `id` 发布后尽量保持稳定，便于后续更新同一组件。
- 新字段应保持向后兼容；旧版 WinIslands 会忽略未知 JSON 字段。
- 插件版本和 WinIslands 版本分别维护。
---

## 10. 2.0.1beta1 扩展能力

### 插件管理器

托盘菜单新增「插件管理」，可以：

- 查看插件名称、ID、版本、描述和权限
- 启用或禁用插件
- 打开单个插件目录
- 打开用户插件目录
- 重新扫描插件

### 扩展组件字段

组件输出新增：

```json
{
  "id": "hello",
  "text": "本地组件",
  "icon": "🧩",
  "color": "#FF6C5CE7",
  "progress": 0.72,
  "click_action": "url",
  "click_value": "https://example.com"
}
```

- `image`：仅支持 `data:image/...;base64,...`，避免插件自动联网
- `progress`：`0..1`
- `color`：`#RRGGBB` / `#AARRGGBB`
- `click_action` / `click_value`：复用上岛动作格式，支持 `url`、`launch`、`command`、`notify`

### 权限与哈希（3.1.8 起强制）

每个插件清单必须提供入口文件的 SHA-256，否则插件只会显示在插件管理器中并被标记为「未校验」，**不会运行**。可以在插件管理器中点击「校验并信任」，由 WinIslands 计算并写回哈希；请仅对你确认来源可信的插件执行此操作。

```json
{
  "permissions": ["network", "filesystem", "processes"],
  "sha256": "入口文件的小写十六进制 SHA-256"
}
```

权限语义：

| 权限 | 语义 |
| --- | --- |
| `network` | 声明插件需要联网；当前版本用于告知与审计，不做操作系统级网络拦截 |
| `filesystem` | 声明插件需要读写文件；当前版本用于告知与审计，不做操作系统级文件隔离 |
| `processes` | 允许插件创建子进程；未声明时 Job Object 将 `ActiveProcessLimit` 设为 1 |

所有插件进程都会被放入 Windows Job Object：主程序退出时自动结束、单插件内存上限 1 GB、未声明 `processes` 时禁止创建子进程。网络与文件系统目前是**声明式权限**，不构成操作系统级硬隔离；请只安装并信任来源可信的插件。

导入插件时，WinIslands 会先展示插件名称与申请权限；如果压缩包声明了 `sha256` 但与实际入口不一致，导入会被拒绝。

### 插件配置注入

`plugin.json` 可以声明配置：

```json
{
  "config": {
    "city": "Shanghai",
    "unit": "metric"
  }
}
```

WinIslands 会把配置转换为环境变量传给插件进程，例如：

- `WINISLANDS_PLUGIN_CONFIG_CITY=Shanghai`
- `WINISLANDS_PLUGIN_CONFIG_UNIT=metric`

插件可以直接读取这些环境变量，不需要读取 WinIslands 主配置。`config_schema` 字段保留给后续自动生成设置界面使用。
