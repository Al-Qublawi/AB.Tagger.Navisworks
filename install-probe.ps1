<#
    Builds and installs the Navisworks 2027 redline-format probe.

    This is a diagnostic tool, not part of the product. It writes a report of the
    redline serialisation format to your Desktop so 2027 support can be built.
    It is installed as its own bundle, separate from the tagger, and changes
    nothing in your models.

    Usage:
        powershell -ExecutionPolicy Bypass -File .\install-probe.ps1
        powershell -ExecutionPolicy Bypass -File .\install-probe.ps1 -Uninstall
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$root       = Split-Path -Parent $MyInvocation.MyCommand.Path
$project    = Join-Path $root 'Probe2027\NwTaggerProbe.csproj'
$bundleDir  = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\ABTaggerProbe.bundle'
$contentDir = Join-Path $bundleDir 'Contents\2027'

function Test-FileLocked([string]$Path) {
    if (-not (Test-Path $Path)) { return $false }
    try {
        $s = [System.IO.File]::Open($Path, 'Open', 'ReadWrite', 'None')
        $s.Close(); $s.Dispose()
        return $false
    } catch { return $true }
}

if ($Uninstall) {
    if (Test-FileLocked (Join-Path $contentDir 'NwTaggerProbe.dll')) {
        Write-Warning 'Navisworks is running and holding the probe open. Close it and try again.'
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

$api = 'C:\Program Files\Autodesk\Navisworks Manage 2027'
if (-not (Test-Path (Join-Path $api 'Autodesk.Navisworks.Api.dll'))) {
    throw "Navisworks Manage 2027 was not found at $api - the probe can only be built against it."
}

Write-Host 'Building the probe...' -ForegroundColor Cyan
& dotnet build $project -c $Configuration -v minimal
if ($LASTEXITCODE -ne 0) { throw "Probe build failed ($LASTEXITCODE)." }

$dll = Join-Path $root "Probe2027\bin\$Configuration\NwTaggerProbe.dll"
if (-not (Test-Path $dll)) { throw "Built assembly not found at $dll" }

if (Test-FileLocked (Join-Path $contentDir 'NwTaggerProbe.dll')) {
    Write-Warning 'Navisworks is running and holding the installed probe open.'
    Write-Warning 'Close Navisworks and run this script again.'
    exit 1
}

New-Item -ItemType Directory -Force -Path $contentDir | Out-Null
Copy-Item $dll $contentDir -Force

# Nw24 is the 2027 series code (release year minus 2003).
$manifest = @'
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage
    SchemaVersion="1.0"
    ProductType="Application"
    Name="AB Tagger Redline Probe"
    AppVersion="1.0.0"
    FriendlyVersion="1.0.0"
    Description="Diagnostic: dumps the Navisworks 2027 redline serialisation format."
    Author="Abdullah Lotfy"
    ProductCode="{7E4A1C08-3D62-4B9F-9C1E-2A5B7F0D4E31}"
    UpgradeCode="{5C93B7A2-6F14-42D8-8E07-B31A9C6E20F5}">

  <CompanyDetails Name="Abdullah Lotfy" />

  <Components Description="AB Tagger Redline Probe for Navisworks Manage 2027">
    <RuntimeRequirements OS="Win64" Platform="NAVMAN" SeriesMin="Nw24" SeriesMax="Nw24" />
    <ComponentEntry
        AppName="AB Tagger Redline Probe"
        AppType="ManagedPlugin"
        Version="1.0.0"
        ModuleName="./Contents/2027/NwTaggerProbe.dll"
        AppDescription="Writes the redline format of every saved viewpoint to a file." />
  </Components>

</ApplicationPackage>
'@

Set-Content -Path (Join-Path $bundleDir 'PackageContents.xml') -Value $manifest -Encoding utf8

Write-Host ''
Write-Host 'Installed:' -ForegroundColor Green
Get-ChildItem $bundleDir -Recurse -File | ForEach-Object {
    Write-Host ("   " + $_.FullName.Substring($bundleDir.Length + 1))
}

Write-Host ''
Write-Host 'How to capture the sample:' -ForegroundColor Green
Write-Host '  1. Start Navisworks Manage 2027 and open any model.'
Write-Host '  2. Review tab -> Text. Type something distinctive, e.g. ABC123.'
Write-Host '  3. Review tab -> Arrow. Drag one arrow.'
Write-Host '  4. Save the viewpoint (Viewpoint tab -> Save Viewpoint).'
Write-Host '  5. Tool Add-ins -> "AB Tagger - Dump Redlines (2027 probe)".'
Write-Host '  6. Send back the ABTagger-Redline-Dump-*.txt file from your Desktop.'
