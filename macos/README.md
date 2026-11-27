# WinIslands for macOS

这是 WinIslands 的 Avalonia macOS 版本源码，与 Windows 版本共享媒体、歌词与上岛推送的核心逻辑。

## 目录

```text
macos/
├─ src/WinIslands.Core/     # 平台无关核心：媒体、歌词、插件、通知、设置
├─ src/WinIslands.Mac/      # Avalonia 桌面外壳与灵动岛视图
├─ tests/                   # 核心单元测试
├─ tools/package_mac.py     # 组装 WinIslands.app、签名、公证、zip 与 DMG
└─ tools/macho_sign.py      # 交叉构建时的 Mach-O ad-hoc 签名助手
```

## 本地构建

需要 .NET 10 SDK：

```bash
dotnet publish src/WinIslands.Mac/WinIslands.Mac.csproj -c Release -r osx-arm64 --self-contained true -o build/publish/osx-arm64
dotnet publish src/WinIslands.Mac/WinIslands.Mac.csproj -c Release -r osx-x64   --self-contained true -o build/publish/osx-x64
python3 tools/package_mac.py
```

本地产物位于 `dist/<版本>/WinIslands-<版本>-osx-arm64.zip` 与
`dist/<版本>/WinIslands-<版本>-osx-x64.zip`，macOS 上还会生成对应 DMG。
`build/` 与 `dist/` 都是构建产物，不进入 Git，也不写入仓库根 `releases/`。

## 正式签名与公证

未签名的构建只做 ad-hoc 签名，首次打开需要右键「打开」。正式发行需要 Apple Developer ID
以及公证凭据：

- `MACOS_SIGN_IDENTITY`：例如 `Developer ID Application: Your Name (TEAMID)`
- `APPLE_ID`、`APPLE_TEAM_ID`、`APPLE_APP_PASSWORD`：用于 `notarytool`

GitHub Actions 的 `release.yml` 会在检测到上述 secrets 时自动执行签名、公证和 staple；
没有凭据时仍会产出可手动打开的 ad-hoc zip。公证失败会让发布工作流失败，避免把未公证包当成正式版。

## 发布前检查

- 在实际 macOS 上验证媒体控制、歌词、多显示器与通知行为。
- 核对 zip/DMG 的 SHA256、Apple 公证状态与应用启动。
- 不要在仓库或 Release 资产中放入证书、密码或个人配置。
