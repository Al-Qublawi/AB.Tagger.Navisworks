# AB Tagger - what its .msi installs and where. Built by build-installer.ps1 through the AB Adv
# Tools kit (shared\ABAdvTools\msi\New-AdvToolsMsi.ps1); the kit README documents every key.
@{
    # Keys the installer's registry state and finds copies left by the retired Setup.exe. Never rename.
    Id          = 'Tagger'
    Name        = 'AB Tagger'
    ArpName     = 'AB Tagger for Navisworks'
    FileName    = 'AB.Tagger'
    Repository  = 'AB.Tagger.Navisworks'

    # First .msi release (1.1.1); the bundle's own UpgradeCode is reused. Never change it.
    UpgradeCode = '96D21C56-955D-4FB7-AC64-039A9DBA6660'

    Description = 'Tags elements in the 3D view with their Quick Properties as native Navisworks redline markup, saves a viewpoint for every tag, and exports the tag list to Excel.'
    NextSteps   = 'Start Navisworks and use AB Tagger on the AB Adv Tools tab.'
    Icon        = 'product.ico'

    # Only me by default, as NwTaggerSetup 1.0 and Setup.exe did - no administrator rights needed.
    Scope        = 'UserOrMachine'
    DefaultScope = 'User'

    NavisworksBundle = @{
        # %AppData% or %ProgramData%\Autodesk\ApplicationPlugins\NwTagger.bundle - the name 1.0 used.
        BundleName     = 'NwTagger.bundle'
        ModuleFileName = 'NwTagger.dll'
        AppName        = 'AB Tagger'
        Description    = 'Tags elements with their Navisworks Quick Properties using native redline markup, and saves a viewpoint for each tag.'
        # PackageContents.xml codes. Keep them stable.
        ProductCode    = '{4B2F3692-ADD6-4AB2-8BDA-ACFE5CCFF689}'
        UpgradeCode    = '{96D21C56-955D-4FB7-AC64-039A9DBA6660}'
        Editions       = @('Manage', 'Simulate')
        # Ticked for the Navisworks releases installed here, as Setup.exe offered.
        Releases       = 'Detected'
    }
}
