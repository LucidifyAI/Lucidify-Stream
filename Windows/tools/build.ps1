<#
.SYNOPSIS
  Build, test and optionally publish Lucid-Stream. Safe to run non-interactively (DevBridge, CI).

.DESCRIPTION
  Steps:
    1. Check Windows and the .NET SDK (8 or later; Visual Studio 2022 installs it with ".NET desktop development").
    2. Stop if a Lucid-Stream built from this folder is running (it locks the files), unless -StopRunning.
    3. Fetch lsl.dll into lib\ if it is missing (skip with -SkipLsl; the app then runs WebSocket-only).
    4. Restore and build every project in LucidStream.sln.
    5. Run the self-tests (skip with -SkipTests).
    6. Optionally publish the Windows app and the command-line host to artifacts\publish and zip them (-Publish).
    7. Optionally start the app (-Run).
  Every dotnet command and its output also go to artifacts\build.log.
  Exit code 0 = everything requested succeeded; 1 = something failed (the message says what).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\build.ps1
  Build Release and run the self-tests.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\build.ps1 -Publish
  Also produce artifacts\publish\LucidStream and a zip. Target PCs need the .NET 8 Desktop Runtime and
  ASP.NET Core Runtime 8 (x64).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\build.ps1 -Publish -SelfContained
  Publish with the .NET runtime included, so the target PC needs nothing installed.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\build.ps1 -Clean -Run -StopRunning
  Close a running copy, rebuild from scratch, test, and start the app.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$Clean,
    [switch]$SkipTests,
    [switch]$SkipLsl,
    [switch]$Publish,
    [switch]$SelfContained,
    [switch]$Run,
    [switch]$StopRunning
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'LucidStream.sln'
$artifacts = Join-Path $root 'artifacts'
$logPath = Join-Path $artifacts 'build.log'
$appProject = Join-Path $root 'src\LucidStream.App\LucidStream.App.csproj'
$cliProject = Join-Path $root 'src\LucidStream.Cli\LucidStream.Cli.csproj'
$testProject = Join-Path $root 'tests\LucidStream.SelfTest\LucidStream.SelfTest.csproj'

$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$script:log = New-Object System.IO.StreamWriter($logPath, $false, (New-Object System.Text.UTF8Encoding($false)))
$script:log.AutoFlush = $true
$script:summary = New-Object System.Collections.Generic.List[string]
$script:dotnet = $null
$totalTimer = [System.Diagnostics.Stopwatch]::StartNew()

# ---------------------------------------------------------------------------- helpers

function Write-Log([string]$text) { $script:log.WriteLine($text) }

function Start-Step([string]$name) {
    Write-Host ''
    Write-Host "==> $name" -ForegroundColor Cyan
    Write-Log ''
    Write-Log "==> $name  ($(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))"
}

function Add-Summary([string]$step, [string]$result) {
    $script:summary.Add(('{0,-28} {1}' -f $step, $result))
}

function Show-Summary {
    Write-Host ''
    Write-Host '---------------- Summary ----------------'
    foreach ($line in $script:summary) { Write-Host $line }
    Write-Host ('Total time: {0:0.0} s' -f $totalTimer.Elapsed.TotalSeconds)
    Write-Host "Log: $logPath"
}

function Stop-Build([string]$message) {
    Write-Host ''
    Write-Host "FAILED: $message" -ForegroundColor Red
    Write-Log "FAILED: $message"
    Show-Summary
    $script:log.Close()
    exit 1
}

# Runs dotnet, echoing output to the console and the log. Returns the exit code.
# Windows PowerShell 5.1 turns native stderr into errors under 'Stop', so this scope uses 'Continue'.
function Invoke-Dotnet([string[]]$arguments) {
    $ErrorActionPreference = 'Continue'
    Write-Log ("> dotnet " + ($arguments -join ' '))
    & $script:dotnet @arguments 2>&1 | ForEach-Object {
        $line = "$_"
        $script:log.WriteLine($line)
        if ($line -match ': error ' -or $line -match '\[FAIL\]') { Write-Host $line -ForegroundColor Red }
        elseif ($line -match ': warning ') { Write-Host $line -ForegroundColor Yellow }
        else { Write-Host $line }
    }
    return $LASTEXITCODE
}

function Find-Dotnet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Path }
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($base) {
            $c = Join-Path $base 'dotnet\dotnet.exe'
            if (Test-Path $c) { return $c }
        }
    }
    return $null
}

function Get-BuildVersion {
    try {
        [xml]$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
        $v = $props.Project.PropertyGroup.Version
        if ($v) { return [string]$v }
    } catch { }
    return '0.0.0'
}

# ---------------------------------------------------------------------------- 1. environment

Start-Step 'Checking tools'
Write-Log "Root: $root"
Write-Log "Configuration: $Configuration"

if ($env:OS -ne 'Windows_NT') { Stop-Build 'Lucid-Stream builds on Windows 10 or 11 (the app uses WPF and the Windows Bluetooth APIs).' }
if (-not (Test-Path $solution)) { Stop-Build "LucidStream.sln not found in $root. Run this script from the repository's tools folder." }

$script:dotnet = Find-Dotnet
if (-not $script:dotnet) {
    Stop-Build 'The .NET SDK was not found. Install the .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0) or add the ".NET desktop development" workload in the Visual Studio Installer.'
}

$ErrorActionPreference = 'Continue'
$sdkLines = @(& $script:dotnet --list-sdks 2>&1 | ForEach-Object { "$_" })
$ErrorActionPreference = 'Stop'
$sdkMajors = @()
foreach ($line in $sdkLines) {
    Write-Log "SDK: $line"
    if ($line -match '^(\d+)\.\d+\.\d+') { $sdkMajors += [int]$Matches[1] }
}
$best = ($sdkMajors | Measure-Object -Maximum).Maximum
if (-not $best -or $best -lt 8) {
    Stop-Build ('No .NET 8 or later SDK found (found: {0}). Install the .NET 8 SDK or update Visual Studio 2022 to 17.8 or later.' -f ($(if ($sdkLines.Count) { $sdkLines -join '; ' } else { 'none' })))
}
$sdkVersion = ($sdkLines | Where-Object { $_ -match '^\d' } | Select-Object -Last 1) -replace '\s.*$', ''
Write-Host "dotnet: $script:dotnet (SDK $sdkVersion)"
Add-Summary 'Tools' "OK (.NET SDK $sdkVersion)"

# ---------------------------------------------------------------------------- 2. running copies lock the output

$running = @(Get-Process -Name 'LucidStream', 'lucid-stream' -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and $_.Path.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase) } catch { $false }
})
if ($running.Count -gt 0) {
    if ($StopRunning) {
        Start-Step 'Stopping running Lucid-Stream'
        foreach ($p in $running) {
            Write-Host "Stopping $($p.ProcessName) (PID $($p.Id))"
            Stop-Process -Id $p.Id -Force
        }
        Start-Sleep -Seconds 1
        Add-Summary 'Running copies' "stopped $($running.Count)"
    } else {
        Stop-Build ('Lucid-Stream from this folder is running (PID {0}) and may be streaming. Close it from the tray icon, or rerun with -StopRunning.' -f (($running | ForEach-Object { $_.Id }) -join ', '))
    }
}

# ---------------------------------------------------------------------------- clean

if ($Clean) {
    Start-Step 'Cleaning'
    # bin and obj directly under each project folder (src\<project>, tests\<project>)
    $dirs = @(Get-ChildItem -Path (Join-Path $root 'src'), (Join-Path $root 'tests') -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Directory | Where-Object { $_.Name -in @('bin', 'obj') } })
    foreach ($d in $dirs) { Remove-Item -LiteralPath $d.FullName -Recurse -Force }
    $publishRoot = Join-Path $artifacts 'publish'
    if (Test-Path $publishRoot) { Remove-Item -LiteralPath $publishRoot -Recurse -Force }
    Write-Host "Removed $($dirs.Count) bin/obj folders."
    Add-Summary 'Clean' 'OK'
}

# ---------------------------------------------------------------------------- 3. liblsl

$lslDll = Join-Path $root 'lib\lsl.dll'
if ($SkipLsl) {
    Add-Summary 'liblsl' $(if (Test-Path $lslDll) { 'present (not checked)' } else { 'skipped: LSL output off' })
} elseif (Test-Path $lslDll) {
    Add-Summary 'liblsl' 'present (lib\lsl.dll)'
} else {
    Start-Step 'Fetching liblsl (lsl.dll)'
    try {
        & (Join-Path $PSScriptRoot 'get-liblsl.ps1')
        Add-Summary 'liblsl' 'downloaded'
    } catch {
        Write-Host "WARNING: liblsl download failed: $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host 'Continuing without it; the app will stream over WebSocket only. Put lsl.dll in lib\ and rebuild to enable LSL.' -ForegroundColor Yellow
        Write-Log "liblsl download failed: $($_.Exception.Message)"
        Add-Summary 'liblsl' 'MISSING: LSL output off (download failed)'
    }
}

# ---------------------------------------------------------------------------- 4. restore and build

Start-Step 'Restoring packages'
$code = Invoke-Dotnet @('restore', $solution, '-nologo', '-v', 'minimal')
if ($code -ne 0) { Add-Summary 'Restore' 'FAILED'; Stop-Build 'dotnet restore failed. NuGet (nuget.org) must be reachable for the Windows SDK reference pack on the first build.' }
Add-Summary 'Restore' 'OK'

Start-Step "Building ($Configuration)"
$buildTimer = [System.Diagnostics.Stopwatch]::StartNew()
$code = Invoke-Dotnet @('build', $solution, '-c', $Configuration, '--no-restore', '-nologo', '-v', 'minimal', '-clp:Summary')
if ($code -ne 0) { Add-Summary 'Build' 'FAILED'; Stop-Build 'Build failed. The errors are listed above and in the log.' }
Add-Summary 'Build' ('OK ({0:0} s)' -f $buildTimer.Elapsed.TotalSeconds)

$appExe = Get-ChildItem -Path (Join-Path $root "src\LucidStream.App\bin\$Configuration") -Recurse -Filter 'LucidStream.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\publish\\' } | Select-Object -First 1
if ($appExe) {
    Write-Host "App: $($appExe.FullName)"
    # Start menu entry, so the app can be found by typing "Lucid-Stream" in Start (and pinned to the taskbar).
    try {
        $programs = [Environment]::GetFolderPath('Programs')
        $lnkPath = Join-Path $programs 'Lucid-Stream.lnk'
        $shell = New-Object -ComObject WScript.Shell
        $lnk = $shell.CreateShortcut($lnkPath)
        $lnk.TargetPath = $appExe.FullName
        $lnk.WorkingDirectory = $appExe.DirectoryName
        $lnk.IconLocation = "$($appExe.FullName),0"
        $lnk.Description = 'Lucid-Stream: stream the LucidPro patch to LSL and WebSockets'
        $lnk.Save()
        Write-Host "Start menu shortcut: $lnkPath"
        Add-Summary 'Start menu shortcut' 'OK (Lucid-Stream)'
    } catch {
        Add-Summary 'Start menu shortcut' "skipped: $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------------------- 5. self-tests

if ($SkipTests) {
    Add-Summary 'Self-tests' 'skipped'
} else {
    Start-Step 'Running self-tests'
    $code = Invoke-Dotnet @('run', '--project', $testProject, '-c', $Configuration, '--no-build')
    if ($code -ne 0) { Add-Summary 'Self-tests' 'FAILED'; Stop-Build 'Build succeeded but self-tests failed (see [FAIL] lines above).' }
    Add-Summary 'Self-tests' 'OK'
}

# ---------------------------------------------------------------------------- 6. publish

if ($Publish) {
    Start-Step 'Publishing'
    $version = Get-BuildVersion
    $flavor = $(if ($SelfContained) { 'self-contained' } else { 'framework-dependent' })
    $sc = $(if ($SelfContained) { 'true' } else { 'false' })
    $publishRoot = Join-Path $artifacts 'publish'
    $appOut = Join-Path $publishRoot 'LucidStream'
    $cliOut = Join-Path $publishRoot 'lucid-stream-cli'
    foreach ($d in @($appOut, $cliOut)) { if (Test-Path $d) { Remove-Item -LiteralPath $d -Recurse -Force } }

    $code = Invoke-Dotnet @('publish', $appProject, '-c', $Configuration, '-r', 'win-x64', '--self-contained', $sc, '-o', $appOut, '-nologo', '-v', 'minimal')
    if ($code -ne 0) { Add-Summary 'Publish' 'FAILED'; Stop-Build 'Publishing the Windows app failed.' }
    $code = Invoke-Dotnet @('publish', $cliProject, '-c', $Configuration, '-r', 'win-x64', '--self-contained', $sc, '-o', $cliOut, '-nologo', '-v', 'minimal')
    if ($code -ne 0) { Add-Summary 'Publish' 'FAILED'; Stop-Build 'Publishing the command-line host failed.' }

    Copy-Item (Join-Path $root 'README.md') $appOut -Force
    if (-not (Test-Path (Join-Path $appOut 'lsl.dll'))) {
        Write-Host 'WARNING: lsl.dll is not in the published app; LSL output will be off there.' -ForegroundColor Yellow
    }
    $zip = Join-Path $artifacts ("LucidStream-$version-win-x64-$flavor.zip")
    if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $appOut '*') -DestinationPath $zip
    Write-Host "Published app: $appOut"
    Write-Host "Published CLI: $cliOut"
    Write-Host "Zip:           $zip"
    if (-not $SelfContained) { Write-Host 'Target PCs need the .NET 8 Desktop Runtime and ASP.NET Core Runtime 8 (x64), or publish with -SelfContained.' }
    Add-Summary 'Publish' "OK ($flavor, $([System.IO.Path]::GetFileName($zip)))"
    $appExe = Get-Item (Join-Path $appOut 'LucidStream.exe')
}

# ---------------------------------------------------------------------------- 7. run

if ($Run) {
    if (-not $appExe) { Stop-Build 'Could not find LucidStream.exe to start.' }
    Start-Step 'Starting Lucid-Stream'
    Start-Process -FilePath $appExe.FullName -WorkingDirectory $appExe.DirectoryName
    Write-Host "Started $($appExe.FullName)"
    Add-Summary 'Run' 'started'
}

Write-Host ''
Write-Host 'BUILD SUCCEEDED' -ForegroundColor Green
Write-Log 'BUILD SUCCEEDED'
Show-Summary
$script:log.Close()
exit 0