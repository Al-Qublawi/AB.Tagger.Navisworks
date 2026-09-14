<#
    Builds the distributable installer, AB.Tagger.Setup.exe.

    One plugin build is produced per Navisworks release whose API assemblies are
    available, each zipped into Setup\payload as Navisworks<year>.zip and embedded
    in the executable. The result is a single self-contained file, built on the
    AB Adv Tools installer engine (shared\ABAdvTools): it detects earlier copies -
    including those NwTaggerSetup.exe 1.0 wrote - offers to remove them, installs,
    and registers in Apps and Features.

    The installer is version-stamped from NwTagger\NwTagger.csproj, so the plugin
    and its installer can never disagree.

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

    Usage:
        powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$root       = Split-Path -Parent $MyInvocation.MyCommand.Path
$plugin     = Join-Path $root 'NwTagger\NwTagger.csproj'
$setup      = Join-Path $root 'Setup\AB.Tagger.Setup.csproj'
$payloadDir = Join-Path $root 'Setup\payload'
$refsDir    = Join-Path $root 'refs'

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

# ---------------------------------------------------------------- payloads

if (Test-Path $payloadDir) { Remove-Item $payloadDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

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

    $stage = Join-Path $root "NwTagger\bin\$Configuration-$year"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

    # No trailing separators in these values: a trailing backslash would
    # escape the closing quote and merge the arguments together.
    & dotnet build $plugin -c $Configuration -v minimal `
        -p:NavisworksDir="$api" `
        -p:OutputSuffix="$year" `
        -p:RedlineBackend="$backend"
    if ($LASTEXITCODE -ne 0) { throw "Plugin build for $year failed ($LASTEXITCODE)." }

    # The plugin ships loose files beside the DLL - ribbon XAML and Images -
    # so the payload is the whole output folder, not just the assembly. Debug
    # symbols help nobody on a user's machine.
    $pack = Join-Path $env:TEMP "abtagger_payload_$year"
    if (Test-Path $pack) { Remove-Item $pack -Recurse -Force }
    Copy-Item $stage $pack -Recurse
    Get-ChildItem $pack -Recurse -Filter *.pdb | Remove-Item -Force

    $zip = Join-Path $payloadDir "Navisworks$year.zip"
    [System.IO.Compression.ZipFile]::CreateFromDirectory($pack, $zip)
    Remove-Item $pack -Recurse -Force

    $built += [pscustomobject]@{ Year = $year; Zip = $zip }
}

if ($built.Count -eq 0) { throw 'No plugin builds were produced - nothing to install.' }

Write-Host ''
Write-Host 'Payloads:' -ForegroundColor Green
$built | ForEach-Object { Write-Host ("   Navisworks{0}.zip  {1:N0} bytes" -f $_.Year, (Get-Item $_.Zip).Length) }

if ($skipped.Count -gt 0) {
    Write-Host ''
    Write-Host 'Not included:' -ForegroundColor Yellow
    $skipped | ForEach-Object { Write-Host "   $_" -ForegroundColor Yellow }
}

# ---------------------------------------------------------------- installer

Write-Host ''
Write-Host 'Building the installer...' -ForegroundColor Cyan
# --no-incremental: the payload is embedded at compile time, and an incremental
# build would happily ship yesterday's plugin inside today's installer.
& dotnet build $setup -c $Configuration -v minimal --no-incremental "-p:Version=$version"
if ($LASTEXITCODE -ne 0) { throw "Installer build failed ($LASTEXITCODE)." }

$exe = Join-Path $root "Setup\bin\$Configuration\AB.Tagger.Setup.exe"
if (-not (Test-Path $exe)) { throw "Installer not found at $exe" }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$final = Join-Path $OutputDirectory 'AB.Tagger.Setup.exe'
Copy-Item $exe $final -Force

# The 1.0 installer's name, so nobody runs a stale copy by mistake.
$stale = Join-Path $OutputDirectory 'NwTaggerSetup.exe'
if (Test-Path $stale) { Remove-Item $stale -Force }

# Confirm the payloads really did travel inside the executable, rather than
# shipping an installer with nothing to install.
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($final))
$payloads = @($asm.GetManifestResourceNames() | Where-Object { $_ -like 'ABAdvTools.Payload.*' })

Write-Host ''
if ($payloads.Count -ne $built.Count) {
    Write-Warning "Expected $($built.Count) payload(s) but the installer contains $($payloads.Count). Do not distribute this build."
    exit 1
}

Write-Host 'Installer ready:' -ForegroundColor Green
Write-Host ("  {0}   ({1:N0} bytes)" -f $final, (Get-Item $final).Length)
$payloads | ForEach-Object { Write-Host "     $_" }
Write-Host ("  SHA256: {0}" -f (Get-FileHash $final -Algorithm SHA256).Hash)

Write-Host ''
Write-Host 'Copy that single file to any machine and run it.' -ForegroundColor Green
Write-Host 'Silent options:  AB.Tagger.Setup.exe /silent   |   /silent /allusers   |   /uninstall /silent'
