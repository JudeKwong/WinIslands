# THIRD_PARTY.md — 第三方组件与许可证

WinIslands 应用本体为 **MIT License**（见 [LICENSE](LICENSE)）。

运行时（发布产物）不依赖任何第三方 NuGet 包，仅使用 .NET 平台自带组件：

| 组件 | 用途 | 许可证 |
| --- | --- | --- |
| .NET 8 Runtime / SDK（Microsoft.NETCore.App, Microsoft.WindowsDesktop.App） | 运行时、WPF、WinForms | MIT |
| Windows SDK 投影 `Microsoft.Windows.SDK.NET.Ref`（CsWinRT） | `Windows.Media.Control`（SMTC）等 WinRT API 互操作 | MIT |
| WPF（WindowsBase, PresentationFramework, PresentationCore） | 界面框架 | MIT |
| Windows Forms（System.Windows.Forms, System.Drawing） | 托盘图标 NotifyIcon、文件夹对话框 | MIT |
| `System.Text.Json`（.NET 内置） | 配置文件序列化 | MIT |
| `System.Net.Http`（.NET 内置） | Cider API / 封面下载 / 在线歌词 | MIT |

> 说明：上述均为 .NET 平台自带组件，随 .NET SDK/Runtime 分发，许可证为 MIT（.NET 使用 MIT，部分组件含 Apache-2.0 头部，详见 .NET 仓库 LICENSE）。

## 开发/测试依赖

| 组件 | 用途 | 许可证 |
| --- | --- | --- |
| xUnit.net v3 (`xunit.v3`) | 单元测试 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 测试宿主 | MIT |

## 图标与资源
- 应用图标与截图：WinIslands 原创（MIT）。

## 未使用
- 无遥测 / 广告 / 商业组件。

## UI 设计参考 / 移植

| 项目 | 用途 | 许可证 |
| --- | --- | --- |
| 岛仔 Daozai（Renly1994/xiaotang-daozai） | 岛体布局、纯黑/液态玻璃配色、字体层级、页标题、圆形徽标、页码豆与组件视觉语言；WPF 端按其设计常量重实现 | MIT |
| LiquidMusic（JudeKwong） | 歌词滚动 ease-out cubic tween、逐行焦点缩放/透明度、程序坞液态玻璃配方（半透明底 + 1px 内高光 + 柔和投影）；WPF 端按相同参数重实现 | 同作者项目，随本仓库分发 |

