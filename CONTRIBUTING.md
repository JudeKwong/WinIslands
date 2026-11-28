# 贡献指南

感谢你愿意改进 WinIslands。无论是缺陷修复、插件、文档还是界面建议，都欢迎提交。

## 开发环境

### Windows

- Windows 10 1809+ / Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Visual Studio 2022（可选，需安装 .NET 桌面开发工作负载）

### macOS

- macOS 13+
- .NET 10 SDK
- Xcode Command Line Tools

## 构建与测试

Windows：

```powershell
dotnet build tests/WinIslands.Tests/WinIslands.Tests.csproj -c Release
dotnet test tests/WinIslands.Tests/WinIslands.Tests.csproj -c Release --no-build
```

macOS：

```bash
cd macos
dotnet build WinIslands.sln -c Release
dotnet test WinIslands.sln -c Release --no-build
```

提交 PR 前请确保两端测试全部通过。涉及启动路径、内存或体积的改动，请同时运行 [性能预算检查](docs/PERFORMANCE_BUDGET.md)。

## 代码约定

- 使用 C# 最新语言版本，保持 nullable 引用类型开启。
- 新增用户可见文案时，优先通过 `Localization` 提供中英文，避免硬编码。
- UI 改动需兼顾高 DPI、多显示器与深色/浅色主题。
- 灵动岛本体与展开模式保持黑色视觉；设置页等普通页面使用毛玻璃。
- 不要引入遥测、后台联网上报或未经说明的网络请求。
- 新增第三方依赖前请说明用途、许可证与体积影响。

## 提交 PR

1. Fork 仓库并从 `main` 创建分支。
2. 保持提交聚焦，一个 PR 解决一类问题。
3. PR 标题长度 4–120 个字符，描述中说明动机、改动与验证方式。
4. 不要提交 `bin`、`obj`、`TestResults`、`artifacts`、`releases` 等生成物。
5. 不要提交证书、私钥、令牌或任何真实用户数据。
6. 等待 Windows / macOS 测试与 PR 门禁全部通过。

CI 会检查：

- Windows 全量测试；
- macOS 构建与核心测试；
- PR 标题与敏感文件扫描；
- CodeQL 静态分析。

## 插件贡献

插件开发请阅读 [插件开发指南](docs/PLUGIN_DEVELOPMENT.md)，可参考 [示例插件](docs/sdk-examples/plugin-hello-world) 与 [插件模板](docs/PLUGIN_TEMPLATE.md)。插件清单必须通过 `plugin.json` 校验，声明最小权限。

## 文档与问题反馈

- 常见问题：[docs/FAQ.md](docs/FAQ.md)
- 故障排查：[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)
- 安全问题：请按 [SECURITY.md](SECURITY.md) 私密报告，不要开公开 Issue。
