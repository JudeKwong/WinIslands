<#
.SYNOPSIS
    校验 WinIslands 插件清单（plugin.json）。

.DESCRIPTION
    规则与 WinIslands 运行时保持一致：id 格式、入口类型与目录约束、
    权限白名单、数值范围、sha256 格式与实际哈希。

.PARAMETER Path
    插件目录，或直接指向 plugin.json。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\validate-plugin.ps1 docs\sdk-examples\plugin-hello-world

.NOTES
    退出码：0 = 通过（可能带警告），1 = 存在错误。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'
$errors = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

function Add-Error([string]$code, [string]$message) { $script:errors.Add("错误 [$code] $message") | Out-Null }
function Add-Warning([string]$code, [string]$message) { $script:warnings.Add("警告 [$code] $message") | Out-Null }

$manifestPath = $Path
if ((Test-Path -LiteralPath $Path -PathType Container)) {
    $manifestPath = Join-Path $Path 'plugin.json'
}
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    Write-Host "找不到 plugin.json：$manifestPath" -ForegroundColor Red
    exit 1
}

$pluginDir = Split-Path -Parent (Resolve-Path -LiteralPath $manifestPath).Path
try {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    Write-Host "plugin.json 不是有效 JSON：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

$knownPermissions = @('network', 'filesystem', 'processes')
$supportedExtensions = @('.exe', '.cmd', '.bat', '.ps1', '.com')

# id
$id = [string]$manifest.id
if ([string]::IsNullOrWhiteSpace($id)) {
    Add-Error 'id-missing' '缺少 id。'
}
elseif ($id -eq '.' -or $id -eq '..' -or $id -notmatch '^[A-Za-z0-9._-]{1,80}$') {
    Add-Error 'id-invalid' 'id 只允许字母、数字、点、横线和下划线，长度 1-80。'
}

# entry
$entry = [string]$manifest.entry
$entryFull = $null
if ([string]::IsNullOrWhiteSpace($entry)) {
    Add-Error 'entry-missing' '缺少 entry（入口文件）。'
}
else {
    $extension = [System.IO.Path]::GetExtension($entry)
    if ($supportedExtensions -notcontains $extension.ToLowerInvariant()) {
        Add-Error 'entry-extension' "不支持的入口类型 '$extension'，仅支持 .exe/.cmd/.bat/.ps1/.com。"
    }
    $entryFull = [System.IO.Path]::GetFullPath((Join-Path $pluginDir $entry))
    $rootWithSep = $pluginDir.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not ($entryFull.StartsWith($rootWithSep, [System.StringComparison]::OrdinalIgnoreCase) -or
              $entryFull.TrimEnd('\', '/') -eq $pluginDir.TrimEnd('\', '/'))) {
        Add-Error 'entry-escape' 'entry 不能跳出插件目录。'
    }
    elseif (-not (Test-Path -LiteralPath $entryFull -PathType Leaf)) {
        Add-Error 'entry-not-found' "入口文件不存在：$entry"
    }
}

# working_directory
if (-not [string]::IsNullOrWhiteSpace([string]$manifest.working_directory)) {
    $workDir = [System.IO.Path]::GetFullPath((Join-Path $pluginDir ([string]$manifest.working_directory)))
    $rootWithSep = $pluginDir.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not ($workDir.StartsWith($rootWithSep, [System.StringComparison]::OrdinalIgnoreCase) -or
              $workDir.TrimEnd('\', '/') -eq $pluginDir.TrimEnd('\', '/'))) {
        Add-Error 'working-directory-escape' 'working_directory 不能跳出插件目录。'
    }
    elseif (-not (Test-Path -LiteralPath $workDir -PathType Container)) {
        Add-Warning 'working-directory-missing' 'working_directory 目录不存在，将回退到插件目录。'
    }
}

# permissions
foreach ($permission in @($manifest.permissions)) {
    if ([string]::IsNullOrWhiteSpace([string]$permission)) { continue }
    if ($knownPermissions -notcontains ([string]$permission).Trim().ToLowerInvariant()) {
        Add-Error 'permission-unknown' "未知权限 '$permission'，仅支持 network / filesystem / processes。"
    }
}

# numeric ranges
$interval = 0
if ($null -ne $manifest.interval_seconds) { $interval = [int]$manifest.interval_seconds }
if ($interval -lt 0) { Add-Error 'interval-negative' 'interval_seconds 不能为负数。' }
elseif ($interval -gt 86400) { Add-Error 'interval-too-large' 'interval_seconds 不能超过 86400。' }
elseif ($interval -gt 0 -and $interval -lt 5) { Add-Warning 'interval-too-small' 'interval_seconds 小于 5 秒，运行时将被提升到 5 秒。' }

$timeout = 10
if ($null -ne $manifest.timeout_seconds) { $timeout = [int]$manifest.timeout_seconds }
if ($timeout -lt 1 -or $timeout -gt 120) { Add-Error 'timeout-range' 'timeout_seconds 必须在 1-120 之间。' }

$outputKb = 256
if ($null -ne $manifest.max_output_kb) { $outputKb = [int]$manifest.max_output_kb }
if ($outputKb -lt 1 -or $outputKb -gt 1024) { Add-Error 'output-range' 'max_output_kb 必须在 1-1024 之间。' }

# arguments
$argsList = @($manifest.arguments)
if ($argsList.Count -gt 64) { Add-Error 'arguments-too-many' 'arguments 最多 64 项。' }
foreach ($argument in $argsList) {
    if ($null -ne $argument -and ([string]$argument).Length -gt 512) {
        Add-Error 'argument-too-long' '单项参数不能超过 512 个字符。'
    }
}

# sha256
$sha = [string]$manifest.sha256
if ([string]::IsNullOrWhiteSpace($sha)) {
    Add-Warning 'sha256-missing' '未提供 sha256；安装后需要在插件管理器中「校验并信任」才能运行。'
}
else {
    $normalized = $sha.Replace('-', '').Trim()
    if ($normalized -notmatch '^[A-Fa-f0-9]{64}$') {
        Add-Error 'sha256-format' 'sha256 必须是 64 位十六进制字符串。'
    }
    elseif ($null -ne $entryFull -and (Test-Path -LiteralPath $entryFull -PathType Leaf)) {
        $actual = (Get-FileHash -LiteralPath $entryFull -Algorithm SHA256).Hash
        if ($actual -ne $normalized) {
            Add-Error 'sha256-mismatch' "sha256 与入口文件实际哈希不一致（实际：$actual）。"
        }
    }
}

# metadata
if ([string]::IsNullOrWhiteSpace([string]$manifest.version)) { Add-Warning 'version-missing' '建议填写 version（例如 1.0.0）。' }
if ([string]::IsNullOrWhiteSpace([string]$manifest.author)) { Add-Warning 'author-missing' '建议填写 author，方便问题追踪。' }
if ([string]::IsNullOrWhiteSpace([string]$manifest.description)) { Add-Warning 'description-missing' '建议填写 description，插件列表会显示这段说明。' }

Write-Host "校验：$manifestPath" -ForegroundColor Cyan
foreach ($warning in $warnings) { Write-Host $warning -ForegroundColor Yellow }
foreach ($errorItem in $errors) { Write-Host $errorItem -ForegroundColor Red }

if ($errors.Count -gt 0) {
    Write-Host "结果：未通过（$($errors.Count) 个错误，$($warnings.Count) 个警告）" -ForegroundColor Red
    exit 1
}

Write-Host "结果：通过（$($warnings.Count) 个警告）" -ForegroundColor Green
exit 0
