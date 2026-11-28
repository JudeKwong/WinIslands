# 性能预算

WinIslands 在发布流程中执行体积与运行时预算检查，防止版本迭代悄悄变慢、变重。

## 预算指标

预算定义在 [build/performance-budget.json](../build/performance-budget.json)：

| 指标 | 上限 | 说明 |
| --- | ---: | --- |
| 便携版可执行文件 | 90 MiB | 单个自包含 `WinIslands.exe` |
| 安装包 | 160 MiB | 通用安装包，同时包含 x64 与 ARM64 载荷 |
| 冷启动 | 12 000 ms | 进程启动到首个空闲采样点 |
| 工作集 | 350 MiB | 采样时的 `Environment.WorkingSet` |
| 私有内存 | 300 MiB | 采样时的 `PrivateMemorySize64` |
| 空闲 CPU | 单核 5% | 启动 12 秒后连续 8 秒采样的进程 CPU 占用 |
| 空闲工作集 | 350 MiB | 同一采样窗口内的 `WorkingSet64` |

这些数值是发布门禁，不是产品承诺的绝对上限；低配设备或开启大量插件时可能更高。

## 测量方式

应用支持诊断参数：

```text
WinIslands.exe --startup-benchmark=<报告路径>
```

启动后会等待界面、绑定、定时器与后台服务进入空闲（约 1.5 秒），再记录：

- 从进程创建到采样的耗时；
- 当前工作集与私有内存；
- 线程数；
- 是否在预算内以及具体超限项。

采样完成后应用立即退出，返回码：

| 返回码 | 含义 |
| ---: | --- |
| 0 | 在预算内 |
| 1 | 采样失败（路径无效、写入失败等） |
| 2 | 超出预算 |

该参数只用于诊断与 CI，不会影响正常启动，也不会出现在新手引导流程里。

## 本地验证

```powershell
# 1. 发布与 CI 相同配置的 x64 自包含版本
dotnet publish src/WinIslands/WinIslands.csproj -c Release -r win-x64 --self-contained true -o artifacts/perf-local

# 2. 采样启动与内存
$report = Join-Path $PWD 'artifacts/perf-local/startup-report.json'
$process = Start-Process -FilePath 'artifacts/perf-local/WinIslands.exe' `
  -ArgumentList "--startup-benchmark=$report" -WindowStyle Hidden -PassThru
$process.WaitForExit(60000) | Out-Null

# 3. 检查体积与运行时报告
./build/check-performance-budget.ps1 `
  -PortableExe 'artifacts/perf-local/WinIslands.exe' `
  -RuntimeReport $report
```

脚本会逐项输出 `PASS` / `FAIL`，任一项超限即以非零退出码结束。

## CI 门禁

Release 工作流在打包后执行两步：

1. **产物体积检查**：对两个架构的便携版和安装包运行 `check-performance-budget.ps1`；
2. **冷启动采样**：启动 x64 发布版并生成 `startup-benchmark.json`，随后校验启动时间、工作集与私有内存。

任一项超预算都会让 Release 构建失败，不会生成正式发布。

## 调整预算

预算只能在有明确理由时调整：

1. 在 PR 中附上调整前后的测量数据；
2. 说明新增功能或依赖为何必须增加预算；
3. 同步修改 `build/performance-budget.json`、`PerformanceBudgetLimits.Fallback` 与本文件；
4. 确认 Windows 与 macOS 测试仍然通过。

## 注意事项

- 冷启动受磁盘、杀毒软件与首次运行影响，CI 数据以发布 runner 为准。
- 采样点选择在首个空闲帧，不包含用户后续操作产生的内存增长。
- 插件在采样前不会主动加载重型任务；插件自身的内存占用不计入本预算。
