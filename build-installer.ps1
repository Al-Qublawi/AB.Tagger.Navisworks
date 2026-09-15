<#
    Builds the distributable installer, dist\AB.Tagger-<version>.msi.

    One plugin build is produced per Navisworks release whose API assemblies are
    available, each staged as one release of a Windows Installer package made by the
    AB Adv Tools kit (shared\ABAdvTools\msi) from installer\Tagger.msi.psd1. The
    package runs no code of its own, so company PCs with Defender's attack surface
    reduction rules install it like any .msi. It installs for Only me (default, no
    administrator rights) or Everyone, and offers to remove a copy the retired
    AB.Tagger.Setup.exe (1.1.0) installed.

    The package is version-stamped from NwTagger\NwTagger.csproj, so the plugin and
    its installer can never disagree.

    Where the API assemblies come from:
      1. A refs\<year> folder in this repo, for releases not installed here.
         Copy these three files out of that release's install folder:
             Autodesk.Navisworks.Api.dll
             Autodesk.Navisworks.ComApi.dll
             Autodesk.Navisworks.Interop.ComApi.dll
      2. An installed Navisworks, e.g.
         C:\Program Files\Autodesk\Navisworks Manage 2026

    Publish the result as a GitHub release asset (tag v<version>) on
    Al-Qublawi/AB.Tagger.Navisworks: installed copies are told about new releases there.

    Requires the WiX CLI 5:  dotnet tool install --global wix --version 5.0.2
                             wix extension add -g WixToolset.UI.wixext/5.0.2

    Usage:
        powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
        powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -DryRun   # what it would do here
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory,
    # After building, show what the package would do on this computer, changing nothing.
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$plugin  = Join-Path $root 'NwTagger\NwTagger.csproj'
$stage   = Join-Path $root 'obj\msi\payload'
$refsDir = Join-Path $root 'refs'
$kitMsi  = Join-Path $root 'shared\ABAdvTools\msi'

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'dist' }

$version = ([xml](Get-Content $plugin)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $plugin." }
Write-Host "AB Tagger $version - installer build" -ForegroundColor Cyan

# Releases the installer knows about, and how each one stores markup.
#
#   Objects  SavedViewpoint.EditRedlines() - 2024, 2025, 2026
#   Json     View.SetRedlines() + ReplaceFromCurrentView - 2027, which made
#            LcOpRedlineList internal and dropped EditRedlines
#
# 2024 is assumed to match 2025; that is checked at build time below, and the
# build simply fails if the assumption is wrong rather than shipping silently.
$knownYears      = 2024, 2025, 2026, 2027
$backends        = @{ 2024 = 'Objects'; 2025 = 'Objects'; 2026 = 'Objects'; 2027 = 'Json' }
$incompatible    = @{}

function Find-ApiDirectory([int]$Year) {
    # Prefer a checked-in refs folder, so a pinned copy wins over a local install.
    $refs = Join-Path $refsDir $Year
    if (Test-Path (Join-Path $refs 'Autodesk.Navisworks.Api.dll')) { return $refs }

    foreach ($edition in 'Manage', 'Simulate') {
        foreach ($base in $env:ProgramFiles, ${env:ProgramFiles(x86)}) {
            if (-not $base) { continue }
            $candidate = Join-Path $base "Autodesk\Navisworks $edition $Year"
            if (Test-Path (Join-Path $candidate 'Autodesk.Navisworks.Api.dll')) { return $candidate }
        }
    }

    return $null
}

# ---------------------------------------------------------------- builds

if (Test-Path $stage) { [System.IO.Directory]::Delete($stage, $true) }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$built   = @()
$skipped = @()

foreach ($year in $knownYears) {

    if ($incompatible.ContainsKey($year)) {
        $skipped += "$year - $($incompatible[$year])"
        continue
    }

    $api = Find-ApiDirectory $year
    if (-not $api) {
        $skipped += "$year - API assemblies not found (install it, or add refs\$year)"
        continue
    }

    $backend = $backends[$year]
    Write-Host "Building for Navisworks $year ($backend backend)" -ForegroundColor Cyan
    Write-Host "   API: $api"

    $output = Join-Path $root "NwTagger\bin\$Configuration-$year"
    if (Test-Path $output) { [System.IO.Directory]::Delete($output, $true) }

    # No trailing separators in these values: a trailing backslash would
    # escape the closing quote and merge the arguments together.
    & dotnet build $plugin -c $Configuration -v minimal `
        -p:NavisworksDir="$api" `
        -p:OutputSuffix="$year" `
        -p:RedlineBackend="$backend"
    if ($LASTEXITCODE -ne 0) { throw "Plugin build for $year failed ($LASTEXITCODE)." }

    # The plugin ships loose files beside the DLL - ribbon XAML and Images -
    # so a release is the whole output folder, not just the assembly. Debug
    # symbols help nobody on a user's machine.
    $to = Join-Path $stage "Navisworks$year"
    Copy-Item $output $to -Recurse
    Get-ChildItem $to -Recurse -Filter *.pdb | ForEach-Object { [System.IO.File]::Delete($_.FullName) }

    $built += $year
}

if ($built.Count -eq 0) { throw 'No plugin builds were produced - nothing to install.' }

Write-Host ''
Write-Host ("Built: Navisworks {0}" -f ($built -join ', ')) -ForegroundColor Green
if ($skipped.Count -gt 0) {
    Write-Host 'Not included:' -ForegroundColor Yellow
    $skipped | ForEach-Object { Write-Host "   $_" -ForegroundColor Yellow }
}

# ---------------------------------------------------------------- installer

Write-Host ''
Write-Host 'Building the .msi...' -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$package = & (Join-Path $kitMsi 'New-AdvToolsMsi.ps1') `
    -Definition (Join-Path $root 'installer\Tagger.msi.psd1') `
    -Payload $stage -Version $version -OutputDirectory $OutputDirectory `
    -WorkDirectory (Join-Path $root 'obj\msi\work')

# Earlier installers left in dist\ would only confuse whoever picks a file to publish.
Get-ChildItem $OutputDirectory -File |
    Where-Object { $_.FullName -ne $package.Path -and ($_.Name -like 'AB.Tagger*' -or $_.Name -like 'NwTaggerSetup*') } |
    ForEach-Object { [System.IO.File]::Delete($_.FullName) }

Write-Host ''
Write-Host 'Installer ready:' -ForegroundColor Green
Write-Host ("  {0}   ({1:N0} bytes)" -f $package.Path, (Get-Item $package.Path).Length)
Write-Host ("  SHA256: {0}" -f $package.Sha256)
Write-Host ''
Write-Host 'Double-click it, or deploy silently:' -ForegroundColor Green
Write-Host "  msiexec /i AB.Tagger-$version.msi /qn              (only me)"
Write-Host "  msiexec /i AB.Tagger-$version.msi /qn ALLUSERS=1   (everyone; elevated prompt)"
Write-Host "  msiexec /x AB.Tagger-$version.msi /qn"

if ($DryRun) {
    & (Join-Path $kitMsi 'Test-AdvToolsMsi.ps1') -Msi $package.Path -WorkDirectory (Join-Path $root 'obj\msi\dryrun') | Out-Null
}
