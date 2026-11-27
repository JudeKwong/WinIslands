<#
.SYNOPSIS
Builds all Windows release assets for one version.

.NOTES
Set WINDOWS_CERTIFICATE_BASE64 and WINDOWS_CERTIFICATE_PASSWORD to sign binaries.
The installer never forces a running WinIslands process to close.
#>
param(
    [Parameter(Mandatory = $false)]
    [string]$Version = '',
    [string]$ArtifactRoot = '',
    [string]$PackageSource = '',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\WinIslands\WinIslands.csproj'
if (-not $Version) {
    $Version = [regex]::Match((Get-Content $proj -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must use major.minor.patch: $Version"
}
if (-not $ArtifactRoot) { $ArtifactRoot = Join-Path $root "artifacts\$Version" }
New-Item -ItemType Directory -Force (Join-Path $ArtifactRoot 'publish'), (Join-Path $ArtifactRoot 'installer') | Out-Null

& (Join-Path $PSScriptRoot 'publish.ps1') -Version $Version `
    -Runtime @('win-x64', 'win-arm64') `
    -OutputDir (Join-Path $ArtifactRoot 'publish') -SkipZip -PackageSource $PackageSource
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }

foreach ($rid in @('win-x64', 'win-arm64')) {
    Copy-Item (Join-Path $ArtifactRoot "publish\$rid\WinIslands.exe") `
        (Join-Path $ArtifactRoot "WinIslands-$Version-$rid.exe") -Force
}

if ($env:WINDOWS_CERTIFICATE_BASE64) {
    $targets = @(
        (Join-Path $ArtifactRoot "WinIslands-$Version-win-x64.exe"),
        (Join-Path $ArtifactRoot "WinIslands-$Version-win-arm64.exe")
    )
    & (Join-Path $PSScriptRoot 'sign-windows.ps1') -Path $targets
    if ($LASTEXITCODE -ne 0) { throw 'Windows signing failed.' }
}
else {
    Write-Warning 'Windows signing skipped: WINDOWS_CERTIFICATE_BASE64 is not configured.'
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $SkipInstaller) {
    if (-not $iscc) {
        throw 'Inno Setup 6 (ISCC.exe) was not found. Install it or pass -SkipInstaller.'
    }
    & $iscc "/DMyAppVersion=$Version" "/DSourceDir=$(Join-Path $ArtifactRoot 'publish')" `
        "/DOutputDir=$(Join-Path $ArtifactRoot 'installer')" (Join-Path $PSScriptRoot 'installer.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }
    $setup = Join-Path $ArtifactRoot "installer\WinIslands-Setup-$Version.exe"
    if (-not (Test-Path $setup)) { throw "Installer not found: $setup" }
    Move-Item -LiteralPath $setup (Join-Path $ArtifactRoot "WinIslands-Setup-$Version.exe") -Force
    if ($env:WINDOWS_CERTIFICATE_BASE64) {
        & (Join-Path $PSScriptRoot 'sign-windows.ps1') -Path (Join-Path $ArtifactRoot "WinIslands-Setup-$Version.exe")
        if ($LASTEXITCODE -ne 0) { throw 'Installer signing failed.' }
    }
}

$hashes = Get-ChildItem -LiteralPath $ArtifactRoot -File | Where-Object { $_.Extension -in '.exe', '.zip' } |
    Sort-Object Name | ForEach-Object {
        "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
    }
$hashes | Set-Content -LiteralPath (Join-Path $ArtifactRoot 'SHA256SUMS.txt') -Encoding ASCII
Write-Host "==> Windows release assets: $ArtifactRoot"
$hashes | ForEach-Object { Write-Host "  $_" }