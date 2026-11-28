# 插件模板与提交指南

这份文档是插件的“社区模板”：从零创建、本地校验、发布和提交到社区的完整流程。
最小可运行示例见 [`docs/sdk-examples/plugin-hello-world`](sdk-examples/plugin-hello-world)。

## 1. 创建插件

```text
my-plugin/
├─ plugin.json
├─ run.ps1          # 或 run.exe / run.cmd / run.bat
└─ README.md
```

复制示例目录后修改三处即可：

1. `plugin.json` 中的 `id`、`name`、`description`、`author`、`version`。
2. 入口脚本的输出内容。
3. 重新计算 `sha256`（见第 3 节）。

`id` 建议使用反向域名格式，例如 `com.yourname.plugin-name`，发布后不要修改。

## 2. 清单字段速查

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `id` | 是 | 1-80 位，仅字母、数字、`.`、`-`、`_` |
| `entry` | 是 | 相对插件目录；仅 `.exe`/`.cmd`/`.bat`/`.ps1`/`.com` |
| `name` / `version` / `author` / `description` | 建议 | 插件列表展示用 |
| `interval_seconds` | 否 | `0` = 不轮询；`>0` 时最小 5 秒 |
| `timeout_seconds` | 否 | 1-120，默认 10 |
| `max_output_kb` | 否 | 1-1024，默认 256 |
| `permissions` | 否 | 仅 `network` / `filesystem` / `processes` |
| `sha256` | 运行必需 | 入口文件 SHA-256（64 位十六进制） |
| `config_schema` | 否 | 参数表单定义，见开发指南 |

## 3. 计算 sha256

PowerShell：

```powershell
(Get-FileHash -Algorithm SHA256 .\run.ps1).Hash
```

把结果写入 `plugin.json` 的 `sha256` 字段。修改入口文件后必须重新计算，
否则插件会被判定为未通过校验而拒绝运行。

## 4. 本地校验

仓库自带校验脚本，规则与运行时一致：

```powershell
powershell -ExecutionPolicy Bypass -File tools\validate-plugin.ps1 .\my-plugin
```

退出码 `0` 表示通过（可能有警告），`1` 表示存在必须修复的错误。
也可以在 WinIslands 的插件管理器中点击「校验并信任」。

## 5. 输出协议自查

- 只向 stdout 输出一个 JSON 对象，日志请写到 stderr。
- 推荐包装为 `{ "components": [ ... ] }`。
- 每个组件必须有稳定的 `id`；`text` 不要过长（紧凑态空间有限）。
- 失败时输出 `{ "components": [] }` 并返回非 0 退出码，便于健康状态统计。
- 不要在插件中读取用户隐私文件；确需访问时在 `permissions` 中声明。

## 6. 发布清单

- [ ] `plugin.json` 通过 `tools/validate-plugin.ps1`
- [ ] `sha256` 与入口文件一致
- [ ] README 说明用途、权限、输出组件
- [ ] 版本号遵循语义化版本
- [ ] 压缩包根目录包含 `plugin.json`（或仅一层子目录）
- [ ] 不含密钥、Token、个人路径

## 7. 提交到社区

在 GitHub Issues 使用「插件展示 / Plugin showcase」模板提交，附上：

- 插件名称与 `id`
- 功能截图或组件示意
- 压缩包或仓库链接
- 声明的权限
- 已在哪个 WinIslands 版本测试

维护者会在本地用同一套校验脚本复核后再收录。
