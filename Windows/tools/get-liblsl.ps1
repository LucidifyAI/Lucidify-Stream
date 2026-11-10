<#
.SYNOPSIS
  Install the Windows x64 liblsl (lsl.dll) into lib\.

.DESCRIPTION
  The build copies lib\lsl.dll next to the app. Without it, Lucid-Stream runs WebSocket-only.

  liblsl's GitHub releases do not always carry a Windows zip, so this installs the official Windows build
  that the pylsl package ships on PyPI. It is pinned to liblsl v1.17.7 (the latest stable liblsl, bundled
  in pylsl 1.18.2). The download and lsl.dll are both checked against pinned SHA-256 hashes, so every PC
  gets the identical file. To use a DLL you obtained yourself, copy it to lib\lsl.dll instead.

  To move to a newer liblsl later, update the four pinned values below together.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\get-liblsl.ps1
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\get-liblsl.ps1 -Force
  Reinstall even if lib\lsl.dll is already the pinned version.
#>
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # the progress bar slows downloads a lot in Windows PowerShell 5.1
Set-StrictMode -Version 2.0
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# ---- pinned source: liblsl v1.17.7 as shipped in pylsl 1.18.2 (win_amd64)
$LiblslVersion = 'v1.17.7'
$WheelUrl = 'https://files.pythonhosted.org/packages/8a/a8/8ccb88b3cd65140e57966472038baa0cebb56bde5e86ad51852faed99cad/pylsl-1.18.2-py2.py3-none-win_amd64.whl'
$WheelSha256 = '3ea2693417c7d79766cebf967250fde78aa1a3ad2b198e40246d36f549dbfde1'
$DllSha256 = '8156d0021794135ce217821cae0e99912753d86d8519e349756d13d99e0292ff'

$root = Split-Path -Parent $PSScriptRoot
$lib = Join-Path $root 'lib'
$target = Join-Path $lib 'lsl.dll'
New-Item -ItemType Directory -Force -Path $lib | Out-Null

function Get-Sha256([string]$path) { return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

if ((Test-Path $target) -and -not $Force -and (Get-Sha256 $target) -eq $DllSha256) {
    Write-Host "liblsl $LiblslVersion is already installed at lib\lsl.dll."
    return
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('liblsl-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    # Saved with a .zip name: a wheel is a zip, and Expand-Archive in PowerShell 5.1 only accepts .zip.
    $zip = Join-Path $work 'pylsl-win_amd64.zip'
    Write-Host "Downloading liblsl $LiblslVersion (official Windows x64 build, from pylsl 1.18.2 on PyPI)..."
    Invoke-WebRequest -Uri $WheelUrl -OutFile $zip -UseBasicParsing -Headers @{ 'User-Agent' = 'lucid-stream-setup' }
    $got = Get-Sha256 $zip
    if ($got -ne $WheelSha256) { throw "Download check failed: expected SHA-256 $WheelSha256, got $got. Nothing was installed." }

    Expand-Archive -Path $zip -DestinationPath (Join-Path $work 'x') -Force
    $dll = Get-ChildItem -Path (Join-Path $work 'x') -Recurse -Filter 'lsl.dll' | Select-Object -First 1
    if (-not $dll) { throw 'lsl.dll was not found in the downloaded package. Nothing was installed.' }
    $got = Get-Sha256 $dll.FullName
    if ($got -ne $DllSha256) { throw "lsl.dll check failed: expected SHA-256 $DllSha256, got $got. Nothing was installed." }

    Copy-Item -LiteralPath $dll.FullName -Destination $target -Force
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

$release = [pscustomobject]@{ tag_name = $LiblslVersion }
Write-Host "liblsl $($release.tag_name) installed at lib\lsl.dll. Build again so it is copied next to the app."