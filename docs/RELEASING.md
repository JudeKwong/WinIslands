# 发布与回滚

本文档面向维护者，描述 WinIslands 的版本号、发布流程、资产命名与回滚策略。

## 版本与渠道

- 正式版本使用三段式版本号 `MAJOR.MINOR.PATCH`，Git 标签为 `v3.1.9`。
- 测试版本通过工作流手动构建并标记为预发布，Beta 渠道会同时接收预发布与正式版本。
- 稳定版用户只接收最新的非预发布版本。
- 发布标签必须是 `v<major>.<minor>.<patch>`，Release 工作流会拒绝其他格式。

## 发布前清单

1. 更新版本号（四处保持一致）：
   - `src/WinIslands/WinIslands.csproj` 的 `Version` / `AssemblyVersion` / `FileVersion` / `InformationalVersion`
   - macOS 对应项目文件
2. 更新 `CHANGELOG.md`，并在 `docs/release-notes/` 补充对应系列说明。
3. 检查 README、官网文案与截图是否与当前界面一致。
4. 运行 Windows 全量测试与 macOS 构建 / 核心测试，确保全部通过。
5. 运行 [性能预算检查](PERFORMANCE_BUDGET.md)，确认体积、启动与内存均在预算内。
6. 本地试运行发布版，至少验证：首次启动、悬停展开、展开收起圆角、滚轮切页、通知展示与过期清理、设置页按钮完整显示。
7. 确认工作区没有 `bin`、`obj`、`artifacts`、`releases` 或任何证书进入提交。
8. 审查 `git diff`，确认没有调试代码、临时日志或测试凭据。

## 发布步骤

```bash
git add -A
git commit -m "release: WinIslands 3.1.9"
git push origin main
git tag v3.1.9
git push origin v3.1.9
```

推送标签后，Release 工作流会：

1. 校验标签格式并提取版本号；
2. 构建 Windows x64 / ARM64 便携版与安装包；
3. 构建 macOS Intel / Apple Silicon 版本并打包 zip / dmg；
4. 在凭据可用时执行 Windows 签名与 macOS 签名、公证、staple；
5. 执行性能预算门禁；
6. 生成 NuGet 依赖报告与 CycloneDX SBOM；
7. 生成 `SHA256SUMS.txt`；
8. 创建 GitHub Release 并上传全部资产；
9. 核对上传资产清单，缺一即失败。

## 资产命名

正式 Release 必须包含：

```text
WinIslands-<version>-win-x64.exe
WinIslands-<version>-win-arm64.exe
WinIslands-Setup-<version>.exe
WinIslands-<version>-osx-x64.zip
WinIslands-<version>-osx-x64.dmg
WinIslands-<version>-osx-arm64.zip
WinIslands-<version>-osx-arm64.dmg
WinIslands-<version>-sbom.cdx.json
SHA256SUMS.txt
```

示例：`WinIslands-3.1.9-win-x64.exe`。

发布后逐项核对：

- 资产数量与命名完全匹配；
- `SHA256SUMS.txt` 覆盖所有二进制与 SBOM；
- 下载 x64 便携版可正常启动；
- Release 说明来自当前 `CHANGELOG.md`；
- 预发布版本已勾选 prerelease，正式版本未勾选。

## 官网更新

官网由 Cloudflare Pages 工作流部署。发布后确认：

- 下载按钮指向最新 Release；
- 版本号与更新日志同步；
- 截图与文案未引用旧界面。

缺少 Cloudflare Secrets 时工作流会跳过部署，此时需要手动发布或补充凭据。

## 回滚策略

1. **优先前滚**：发现回归时发布补丁版本，保留完整历史，避免用户被迫降级。
2. **阻止继续扩散**：在 GitHub Release 中将问题版本标记为 pre-release，或编辑说明提示用户暂缓升级。
3. **恢复旧版为最新**：必要时把上一个稳定版本重新设为 latest；更新器按版本号选择，不会自动降级，用户可手动下载旧版。
4. **不删除用户数据**：回滚不得清空设置、歌词缓存或插件目录；如数据结构有变更，需提供迁移与回退逻辑。
5. **记录事件**：在 CHANGELOG 与 Release 说明中写明问题、影响范围与修复版本。

## 本地产物清理

按项目文件管理要求，发布完成后本地只保留最新正式版 Windows x64 产物：

- 删除 Beta、旧正式版与 macOS 产物；
- 保留 `releases/<version>/win-x64/WinIslands-<version>-win-x64.exe`；
- 不把本地发布目录提交到 Git。
