param(
    [string[]]$PortableExe = @(),
    [string]$Installer = '',
    [string]$RuntimeReport = '',
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'performance-budget.json')
)

$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$failures = New-Object System.Collections.Generic.List[string]

function Test-ArtifactFile {
    param([string]$Label, [string]$Path, [double]$MaxMiB)
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $failures.Add("$Label is missing: $Path")
        return
    }
    $bytes = (Get-Item -LiteralPath $Path).Length
    $actualMiB = $bytes / 1MB
    $state = if ($actualMiB -le $MaxMiB) { 'PASS' } else { 'FAIL' }
    Write-Host ("[{0}] {1}: {2:N1} MiB (limit {3:N1} MiB) - {4}" -f $state, $Label, $actualMiB, $MaxMiB, $Path)
    if ($actualMiB -gt $MaxMiB) {
        $failures.Add("$Label is $($actualMiB.ToString('0.0')) MiB; limit is $($MaxMiB.ToString('0.0')) MiB.")
    }
}

foreach ($exe in $PortableExe) {
    Test-ArtifactFile -Label 'Portable executable' -Path $exe -MaxMiB ([double]$config.portableExeMaxMiB)
}
Test-ArtifactFile -Label 'Installer' -Path $Installer -MaxMiB ([double]$config.installerMaxMiB)

if (-not [string]::IsNullOrWhiteSpace($RuntimeReport)) {
    if (-not (Test-Path -LiteralPath $RuntimeReport -PathType Leaf)) {
        $failures.Add("Runtime report is missing: $RuntimeReport")
    }
    else {
        $report = Get-Content -LiteralPath $RuntimeReport -Raw | ConvertFrom-Json
        $checks = @(
            @{ Name = 'Startup'; Value = [double]$report.startupMilliseconds; Limit = [double]$config.startupMaxMilliseconds; Unit = 'ms' },
            @{ Name = 'Working set'; Value = [double]$report.workingSetMiB; Limit = [double]$config.workingSetMaxMiB; Unit = 'MiB' },
            @{ Name = 'Private memory'; Value = [double]$report.privateMemoryMiB; Limit = [double]$config.privateMemoryMaxMiB; Unit = 'MiB' }
        )
        foreach ($check in $checks) {
            $state = if ($check.Value -le $check.Limit) { 'PASS' } else { 'FAIL' }
            Write-Host ("[{0}] {1}: {2:N1} {3} (limit {4:N1} {3})" -f $state, $check.Name, $check.Value, $check.Unit, $check.Limit)
            if ($check.Value -gt $check.Limit) {
                $failures.Add("$($check.Name) is $($check.Value) $($check.Unit); limit is $($check.Limit) $($check.Unit).")
            }
        }
        if (-not [bool]$report.withinBudget) {
            $failures.Add('Application reported withinBudget=false.')
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host 'Performance budget PASSED.'