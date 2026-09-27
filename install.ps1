<#
    Builds the Element Tagger and installs it as an Autodesk application bundle
    for the current user - the fast loop while working on the code. For anything
    you would give to someone else, build the .msi (build-installer.ps1).

    Usage:
        powershell -ExecutionPolicy Bypass -File .\install.ps1
        powershell -ExecutionPolicy Bypass -File .\install.ps1 -Year 2025
        powershell -ExecutionPolicy Bypass -File .\install.ps1 -Year 2026,2027
        powershell -ExecutionPolicy Bypass -File .\install.ps1 -Configuration Debug
        powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall

    With no -Year it builds for every Navisworks release installed on this
    machine. Each release needs its own build: Navisworks binds plugins by strong
    name, and 2027 stores markup by a different route (see the README).

    The bundle goes to the per-user plugin folder, so no admin rights are needed:
        %APPDATA%\Autodesk\ApplicationPlugins\NwTagger.bundle

    A copy installed for everyone (%PROGRAMDATA%) wins over this one, or fights
    with it - the script says so rather than leaving you testing the wrong
    binary.
#>

[CmdletBinding()]
param(
    # Windows PowerShell 5.1 will not validate a range per element of an array,
    # so the years are checked below instead.
    [int[]]$Year,

    [string]$Configuration = 'Release',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$root       = Split-Path -Parent $MyInvocation.MyCommand.Path
$project    = Join-Path $root 'NwTagger\NwTagger.csproj'
$bundleName = 'NwTagger.bundle'
$bundleDir  = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\$bundleName"
$machineDir = Join-Path $env:PROGRAMDATA "Autodesk\ApplicationPlugins\$bundleName"

# 2027 made LcOpRedlineList internal and dropped SavedViewpoint.EditRedlines, so
# it stores markup as JSON on the view instead - one source file differs.
$backends = @{ 2024 = 'Objects'; 2025 = 'Objects'; 2026 = 'Objects'; 2027 = 'Json' }

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

# Where a release's API assemblies are: a pinned copy in refs\<year> first, then
# the installed product.
function Get-ApiDirectory {
    # Not named Year on purpose: PowerShell variable names are case
    # insensitive, so a parameter called Year here is the script's own $Year.
    param([int]$Release)

    $refs = Join-Path $root "refs\$Release"
    if (Test-Path (Join-Path $refs 'Autodesk.Navisworks.Api.dll')) { return $refs }

    foreach ($edition in 'Manage', 'Simulate') {
        $dir = "C:\Program Files\Autodesk\Navisworks $edition $Release"
        if (Test-Path (Join-Path $dir 'Autodesk.Navisworks.Api.dll')) { return $dir }
    }

    return $null
}

function Get-InstalledYears {
    $found = @()

    foreach ($release in 2024..2027) {
        foreach ($edition in 'Manage', 'Simulate') {
            if (Test-Path "C:\Program Files\Autodesk\Navisworks $edition $release\Roamer.exe") {
                $found += $release
                break
            }
        }
    }

    return $found
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

foreach ($requested in $Year) {
    if ($requested -lt 2024 -or $requested -gt 2027) {
        throw "Unsupported release: $requested. This add-in builds for 2024 to 2027."
    }
}

if (-not $Year -or $Year.Count -eq 0) {
    $Year = Get-InstalledYears
    if (-not $Year) { throw 'No Navisworks installation found. Pass -Year to build anyway.' }
    Write-Host ("Navisworks found here: " + ($Year -join ', ')) -ForegroundColor Cyan
}

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = '1.0.0' }

$built = @()

foreach ($release in ($Year | Sort-Object -Unique)) {
    $api = Get-ApiDirectory $release

    if (-not $api) {
        Write-Warning "No API assemblies for $release (install it, or drop them in refs\$release). Skipped."
        continue
    }

    $backend = $backends[$release]
    $outSuffix = "$Configuration-$release"
    $buildDir = Join-Path $root "NwTagger\bin\$outSuffix"

    Write-Host ""
    Write-Host "Building $release ($backend markup, $Configuration)..." -ForegroundColor Cyan
    Write-Host "   API: $api" -ForegroundColor DarkGray

    & dotnet build $project -c $Configuration -v minimal --nologo `
        -p:OutputSuffix=$release -p:RedlineBackend=$backend -p:NavisworksDir=$api

    if ($LASTEXITCODE -ne 0) { throw "Build failed for $release with exit code $LASTEXITCODE." }

    $dll = Join-Path $buildDir 'NwTagger.dll'
    if (-not (Test-Path $dll)) { throw "Built assembly not found at $dll" }

    $contentDir = Join-Path $bundleDir "Contents\$release"
    $target = Join-Path $contentDir 'NwTagger.dll'

    if (Test-FileLocked $target) {
        Write-Warning "The installed $release plugin is locked by a running Navisworks. Close it and run again."
        exit 1
    }

    New-Item -ItemType Directory -Force -Path $contentDir | Out-Null

    # The whole build output, not just the assembly: Navisworks reads the ribbon
    # XAML and the Images folder as loose files sitting beside the DLL.
    Copy-Item (Join-Path $buildDir '*') $contentDir -Recurse -Force

    $built += $release
}

if (-not $built) { throw 'Nothing was built.' }

# PackageContents.xml, written for exactly the releases just built. Series codes
# are the year minus 2003, so 2024 is Nw21 and 2027 is Nw24.
$components = foreach ($release in $built) {
    $series = "Nw$($release - 2003)"

    @"
  <Components Description="AB Tagger for Navisworks $release">
    <RuntimeRequirements OS="Win64" Platform="NAVMAN|NAVSIM" SeriesMin="$series" SeriesMax="$series" />
    <ComponentEntry
        AppName="AB Tagger $release"
        AppType="ManagedPlugin"
        Version="$version"
        ModuleName="./Contents/$release/NwTagger.dll"
        AppDescription="Quick-property element tagging with leader lines and auto-saved viewpoints." />
  </Components>
"@
}

$packageContents = @"
<?xml version="1.0" encoding="utf-8"?>
<!-- Written by install.ps1 for a development install. The .msi writes its own. -->
<ApplicationPackage
    SchemaVersion="1.0"
    ProductType="Application"
    Name="AB Tagger"
    AppVersion="$version"
    FriendlyVersion="$version"
    Description="Tags elements with their Navisworks Quick Properties using native redline markup, and saves a viewpoint for each tag."
    Author="Abdullah Lotfy"
    ProductCode="{4B2F3692-ADD6-4AB2-8BDA-ACFE5CCFF689}"
    UpgradeCode="{96D21C56-955D-4FB7-AC64-039A9DBA6660}">

  <CompanyDetails Name="Abdullah Lotfy" />

$($components -join "`r`n")
</ApplicationPackage>
"@

Set-Content -Path (Join-Path $bundleDir 'PackageContents.xml') -Value $packageContents -Encoding UTF8

# Anything left over from an earlier install for a release not built this time
# would still be loaded, so say so.
Get-ChildItem (Join-Path $bundleDir 'Contents') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $built -notcontains [int]$_.Name } |
    ForEach-Object { Write-Warning "Contents\$($_.Name) is left over from an earlier install and is no longer listed. Delete it if you do not want it." }

Write-Host ''
Write-Host "Installed AB Tagger $version for $($built -join ', ')" -ForegroundColor Green
Write-Host "  $bundleDir"

# The one that silently wastes an afternoon: an all-users copy of the same
# bundle, which declares the same plugin ids.
if (Test-Path $machineDir) {
    $machineYears = Get-ChildItem (Join-Path $machineDir 'Contents') -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Name }

    $clash = $built | Where-Object { $machineYears -contains "$_" }

    Write-Host ''
    Write-Warning "An all-users copy is installed as well: $machineDir"

    if ($clash) {
        Write-Warning ("It covers " + ($clash -join ', ') + " too, and declares the same plugin ids.")
        Write-Warning 'Navisworks will load one of them and quietly ignore the other, so you cannot tell'
        Write-Warning 'which binary you are testing. Uninstall it (Apps and Features > AB Tagger) first.'
    }
}

if (Test-NavisworksRunning) {
    Write-Host ''
    Write-Host 'Navisworks is currently running. It scans for plugins at startup,' -ForegroundColor Yellow
    Write-Host 'so RESTART Navisworks before the new build appears.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'In Navisworks, open the panel from either:' -ForegroundColor Green
Write-Host '  View > Windows > Element Tagger      (dockable panel)'
Write-Host '  AB Adv Tools > AB Tagger             (toggles the same panel)'
Write-Host 'Diagnostics: Tool Add-ins > "AB Tagger - Self test"'
