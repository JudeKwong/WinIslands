<#
.SYNOPSIS
Builds self-contained WinIslands binaries into a staging directory.

.DESCRIPTION
The default output is artifacts/publish/<rid>. Release assets are copied to the exact
GitHub asset names by build/build-release.ps1. Local releases/ is intentionally not used
for staging so build products never mix with the minimal local release artifact.
#>
param(
    [string]$Version = '',
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]]$Runtime = @('win-x64', 'win-arm64'),
    [string]$OutputDir = '',
    [switch]$FrameworkDependent,
    [switch]$SkipZip,
    [string]$PackageSource = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\WinIslands\WinIslands.csproj'
if (-not $Version) {
    $Version = [regex]::Match((Get-Content $proj -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
}
if (-not $OutputDir) { $OutputDir = Join-Path $root 'artifacts\publish' }
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

foreach ($rid in $Runtime) {
    $out = Join-Path $OutputDir $rid
    # Restore per-RID assets explicitly. This avoids the .NET 10 CLI sometimes hanging
    # when `dotnet publish` starts its implicit restore, and makes failures easier to read.
    $restoreArgs = @('restore', $proj, '-r', $rid, "-p:Version=$Version")
    if ($PackageSource) { $restoreArgs += @('--source', $PackageSource) }
    Write-Host "==> dotnet $($restoreArgs -join ' ')"
    & $dotnet @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw "restore failed for $rid" }

    $publishArgs = @(
        'publish', $proj, '-c', 'Release', '-r', $rid, '--no-restore',
        "-p:Version=$Version",
        "-p:AssemblyVersion=$Version.0",
        "-p:FileVersion=$Version.0",
        "-p:InformationalVersion=$Version",
        '-o', $out
    )
    if ($FrameworkDependent) { $publishArgs += @('--self-contained', 'false') }
    else { $publishArgs += @('--self-contained', 'true') }

    Write-Host "==> dotnet $($publishArgs -join ' ')"
    & $dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

    if (-not $SkipZip) {
        $zip = Join-Path $OutputDir "WinIslands-$Version-$rid.zip"
        if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
        Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
        Write-Host "==> zip created: $zip"
    }
    Write-Host "==> output: $out"
}