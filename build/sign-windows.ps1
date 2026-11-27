<#
.SYNOPSIS
Signs one or more Windows executables with an Authenticode certificate.

Certificate input is read from environment variables so secrets never touch the repository:
  WINDOWS_CERTIFICATE_BASE64  Base64-encoded .pfx file
  WINDOWS_CERTIFICATE_PASSWORD  PFX password
  WINDOWS_TIMESTAMP_URL         Optional RFC 3161 timestamp URL
#>
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Path
)

$ErrorActionPreference = 'Stop'

if (-not $env:WINDOWS_CERTIFICATE_BASE64) {
    throw 'WINDOWS_CERTIFICATE_BASE64 is not configured.'
}

$signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
if (-not $signtool) {
    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    ) | Where-Object { Test-Path $_ }
    $signtool = Get-ChildItem $roots -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $signtool) {
    throw 'signtool.exe was not found. Install the Windows SDK or use a self-hosted signing runner.'
}

$pfx = Join-Path ([IO.Path]::GetTempPath()) ("winislands-sign-" + [guid]::NewGuid().ToString('N') + '.pfx')
try {
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:WINDOWS_CERTIFICATE_BASE64))
    $timestamp = if ($env:WINDOWS_TIMESTAMP_URL) { $env:WINDOWS_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }
    foreach ($file in $Path) {
        if (-not (Test-Path -LiteralPath $file)) { throw "Signing target not found: $file" }
        & $signtool sign /fd SHA256 /td SHA256 /tr $timestamp /f $pfx /p $env:WINDOWS_CERTIFICATE_PASSWORD $file
        if ($LASTEXITCODE -ne 0) { throw "Signing failed: $file" }
        & $signtool verify /pa /v $file
        if ($LASTEXITCODE -ne 0) { throw "Signature verification failed: $file" }
    }
}
finally {
    if (Test-Path -LiteralPath $pfx) { Remove-Item -LiteralPath $pfx -Force }
}