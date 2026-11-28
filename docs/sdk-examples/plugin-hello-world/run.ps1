# WinIslands 示例插件：在灵动岛显示当前物理内存占用。
# 插件通过 stdout 返回 UTF-8 JSON；任何异常都不应影响主程序。
$ErrorActionPreference = 'Stop'

try {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    $total = [double]$os.TotalVisibleMemorySize
    $free  = [double]$os.FreePhysicalMemory
    $used  = if ($total -gt 0) { [math]::Round((1 - ($free / $total)) * 100) } else { 0 }

    $payload = @{
        components = @(
            @{
                id               = 'hello-memory'
                text             = "内存 $used%"
                icon             = '🧠'
                tooltip          = '示例插件：当前物理内存占用'
                show_when_playing = $true
                show_when_idle    = $true
                order            = 50
            }
        )
    }
    $payload | ConvertTo-Json -Depth 6 -Compress
}
catch {
    # 失败时输出空组件数组，主程序会保留上一次结果或显示插件错误状态。
    @{ components = @() } | ConvertTo-Json -Depth 4 -Compress
}
