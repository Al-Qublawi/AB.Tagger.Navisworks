<#
    Builds the Element Tagger and installs it as an Autodesk application bundle.

    Usage:
        powershell -ExecutionPolicy Bypass -File .\install.ps1
        powershell -ExecutionPolicy Bypass -File .\install.ps1 -Configuration Debug
        powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall

    The bundle goes to the per-user plugin folder, so no admin rights are needed:
        %APPDATA%\Autodesk\ApplicationPlugins\NwTagger.bundle
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$root       = Split-Path -Parent $MyInvocation.MyCommand.Path
$project    = Join-Path $root 'NwTagger\NwTagger.csproj'
$bundleName = 'NwTagger.bundle'
$bundleDir  = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\$bundleName"
$contentDir = Join-Path $bundleDir 'Contents\2026'

function Test-NavisworksRunning {
    $procs = Get-Process -Name 'Roamer' -ErrorAction SilentlyContinue
    return ($null -ne $procs)
}

# Navisworks only locks the plugin once it has loaded it, which happens at
# startup. A first install into a fresh bundle folder is therefore safe even
# while Navisworks is open - so test the actual file rather than assuming.
function Test-FileLocked {
    param([string]$Path)

    if (-not (Test-Path $Path)) { return $false }

    try {
        $stream = [System.IO.File]::Open($Path, 'Open', 'ReadWrite', 'None')
        $stream.Close()
        $stream.Dispose()
        return $false
    } catch {
        return $true
    }
}

if ($Uninstall) {
    if (Test-NavisworksRunning) {
        Write-Warning 'Navisworks is running. Close it first, then re-run with -Uninstall.'
        exit 1
    }

    if (Test-Path $bundleDir) {
        Remove-Item $bundleDir -Recurse -Force
        Write-Host "Removed $bundleDir" -ForegroundColor Yellow
    } else {
        Write-Host 'Nothing to uninstall.' -ForegroundColor Yellow
    }

    exit 0
}

Write-Host "Building ($Configuration)..." -ForegroundColor Cyan
& dotnet build $project -c $Configuration -v minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

$dll = Join-Path $root "NwTagger\bin\$Configuration\NwTagger.dll"
if (-not (Test-Path $dll)) { throw "Built assembly not found at $dll" }

$target = Join-Path $contentDir 'NwTagger.dll'

if (Test-FileLocked $target) {
    Write-Warning 'The installed plugin is locked by a running Navisworks session.'
    Write-Warning 'Close Navisworks and run this script again.'
    exit 1
}

$navisworksOpen = Test-NavisworksRunning

New-Item -ItemType Directory -Force -Path $contentDir | Out-Null

Copy-Item (Join-Path $root 'deploy\PackageContents.xml') $bundleDir -Force

# The whole build output, not just the assembly: Navisworks reads the ribbon
# XAML and the Images folder as loose files sitting beside the DLL.
$buildDir = Split-Path -Parent $dll
Copy-Item (Join-Path $buildDir '*') $contentDir -Recurse -Force

Write-Host ''
Write-Host 'Installed:' -ForegroundColor Green
Write-Host "  $bundleDir"
Get-ChildItem $bundleDir -Recurse -File | ForEach-Object {
    Write-Host ("   " + $_.FullName.Substring($bundleDir.Length + 1))
}

Write-Host ''

if ($navisworksOpen) {
    Write-Host 'Navisworks is currently running. It scans for plugins at startup,' -ForegroundColor Yellow
    Write-Host 'so RESTART Navisworks before the Element Tagger appears.' -ForegroundColor Yellow
    Write-Host ''
}

Write-Host 'In Navisworks Manage 2026, open the panel from either:' -ForegroundColor Green
Write-Host '  View > Windows > Element Tagger      (dockable panel)'
Write-Host '  AB Adv Tools > AB Tagger             (toggles the same panel)'
