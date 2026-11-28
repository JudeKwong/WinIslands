# 供应链与构建验证

本文件说明 WinIslands 如何降低依赖被篡改、构建不可追溯与发布资产被替换的风险。

## 依赖与工作流锁定

- 所有 GitHub Actions 的 `uses:` 都固定到完整提交 SHA，并在行尾保留版本注释，避免标签被移动导致执行未知代码。
- NuGet 依赖通过 `PackageReference` 管理，CI 使用锁定的 SDK 版本还原。
- 发布构建使用 .NET 10 SDK，Windows 与 macOS 分别在对应 runner 上构建。

## 自动依赖更新

[.github/dependabot.yml](../.github/dependabot.yml) 每周检查：

- 根目录 NuGet 依赖；
- `macos` 目录 NuGet 依赖；
- GitHub Actions；
- `build` 目录 Python 依赖。

同类依赖按组提交，默认忽略主版本升级，减少破坏性变更直接进入主分支。

## 静态分析

[.github/workflows/codeql.yml](../.github/workflows/codeql.yml) 在推送、PR 与每周计划任务中运行 CodeQL：

- C#：手动构建 Windows 解决方案后分析；
- JavaScript / TypeScript：对官网与脚本做无构建分析。

结果上传到 GitHub Security 面板，并遵循仓库的告警处理流程。

## SBOM

每个正式版本都会生成 CycloneDX 1.5 软件物料清单：

```text
WinIslands-<version>-sbom.cdx.json
```

生成脚本为 [build/generate_sbom.py](../build/generate_sbom.py)，内容包含：

- 发布资产（exe / zip / dmg）的名称、大小与 SHA-256；
- 从 `nuget-windows.json` 与 `nuget-macos.json` 提取的 NuGet 组件及版本；
- 生成时间与工具信息。

发布工作流会先用 `json.tool` 校验 SBOM 可解析，再上传为 Release 资产。

本地生成示例：

```powershell
python build/generate_sbom.py --artifacts artifacts --version 3.1.9 --output artifacts/WinIslands-3.1.9-sbom.cdx.json
```

## 构建来源证明

发布工作流使用 GitHub 官方 `actions/attest-build-provenance` 为 Windows 便携版、安装包以及 macOS 的 zip / dmg 生成构建来源证明（SLSA build provenance）。证明由 GitHub OIDC 签发，记录构建仓库、提交、工作流与产物摘要，可用以下命令验证：

```bash
gh attestation verify WinIslands-3.1.9-win-x64.exe --repo JudeKwong/WinIslands
```

来源证明与 SHA256 校验和互为补充：校验和防篡改，来源证明说明产物确实由本仓库的发布工作流构建。

## 校验和

Release 会生成 `SHA256SUMS.txt`，覆盖：

- `WinIslands-<version>-win-x64.exe`
- `WinIslands-<version>-win-arm64.exe`
- `WinIslands-Setup-<version>.exe`
- `WinIslands-<version>-osx-x64.zip` / `.dmg`
- `WinIslands-<version>-osx-arm64.zip` / `.dmg`
- `WinIslands-<version>-sbom.cdx.json`

下载后校验：

```powershell
# Windows
Get-FileHash .\WinIslands-3.1.9-win-x64.exe -Algorithm SHA256
```

```bash
# macOS
shasum -a 256 WinIslands-3.1.9-osx-arm64.dmg
```

将结果与 `SHA256SUMS.txt` 中对应行比较。校验和文件本身也随 Release 发布。

## 签名与公证

- **Windows**：发布工作流在配置了 `WINDOWS_CERTIFICATE_BASE64` 与 `WINDOWS_CERTIFICATE_PASSWORD` 时对便携版与安装包做 Authenticode 签名；未配置时跳过签名，仅用于内部测试构建。
- **macOS**：打包脚本在配置 `MACOS_SIGN_IDENTITY`、`APPLE_ID`、`APPLE_TEAM_ID`、`APPLE_APP_PASSWORD` 时执行 codesign、公证与 staple；缺少凭据时不会伪造签名。

证书、密码与 Apple 凭据只存放在 GitHub Actions Secrets，不进入仓库、日志或发布资产。

## 漏洞响应

- 依赖漏洞由 Dependabot 与 CodeQL 发现后按严重程度处理；
- 安全问题的私密报告流程见 [SECURITY.md](../SECURITY.md)；
- 修复版本优先通过补丁发布，并在 CHANGELOG 中说明影响范围。
