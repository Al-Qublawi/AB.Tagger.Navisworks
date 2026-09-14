using System;
using System.Collections.Generic;
using ABAdvTools.Setup;

namespace NwTaggerSetup
{
    /// <summary>
    /// What AB Tagger installs, and how its earlier releases are recognised.
    ///
    /// Layout: an Autodesk application bundle, NwTagger.bundle, in ApplicationPlugins - the
    /// current user by default (as 1.0's NwTaggerSetup.exe did, no administrator rights), or all
    /// users. The bundle name is unchanged from 1.0 so nothing Navisworks remembers moves.
    ///
    /// Earlier versions it recognises:
    ///   - a bundle written by NwTaggerSetup.exe 1.0 or install.ps1, in either scope
    ///   - an install made by this installer, through its Apps and Features entry
    /// </summary>
    internal sealed class TaggerSetup : SetupProduct
    {
        private readonly NavisworksBundle _bundle = new NavisworksBundle
        {
            BundleName = "NwTagger.bundle",
            ModuleFileName = "NwTagger.dll",
            AppName = "AB Tagger",
            Description = "Tags elements with their Navisworks Quick Properties using native redline markup, and saves a viewpoint for each tag.",
            ProductCode = "{4B2F3692-ADD6-4AB2-8BDA-ACFE5CCFF689}",
            UpgradeCode = "{96D21C56-955D-4FB7-AC64-039A9DBA6660}"
        };

        public override string Id { get { return "Tagger"; } }
        public override string Name { get { return "AB Tagger"; } }
        public override string ArpDisplayName { get { return "AB Tagger for Navisworks"; } }
        public override string GitHubRepository { get { return "AB.Tagger.Navisworks"; } }
        public override InstallScope DefaultScope { get { return InstallScope.CurrentUser; } }
        public override string[] BlockingProcesses { get { return new[] { AutodeskLocator.NavisworksProcess }; } }

        /// <summary>
        /// NwTaggerSetup.exe 1.0 removed without a window on a bare /uninstall (current user, or
        /// /allusers). Scripts written for it keep working; Apps and Features still gets the window.
        /// </summary>
        public override bool UninstallSwitchIsSilent { get { return true; } }

        public override string Description
        {
            get
            {
                return "Tags elements in the 3D view with their Quick Properties as native Navisworks redline " +
                       "markup, saves a viewpoint for every tag, and exports the tag list to Excel.";
            }
        }

        public override string HostSummary
        {
            get { return "Autodesk Navisworks Manage and Simulate " + Payload.YearRange("Navisworks"); }
        }

        public override string NextSteps
        {
            get { return "Start Navisworks and use AB Tagger on the AB Adv Tools tab."; }
        }

        public override List<HostTarget> FindTargets(SetupContext ctx)
        {
            var targets = AutodeskLocator.FindNavisworks(2024, 2035);
            foreach (HostTarget target in targets)
            {
                target.PayloadAvailable = Payload.Has(NavisworksBundle.PayloadName(target.Year));
                if (!target.PayloadAvailable) target.Note = "this installer has no build for " + target.Year;
            }
            return targets;
        }

        public override List<ExistingInstall> FindExistingInstalls(SetupContext ctx)
        {
            var found = new List<ExistingInstall>();

            foreach (InstallScope scope in new[] { InstallScope.AllUsers, InstallScope.CurrentUser })
            {
                ExistingInstall registered = RegisteredInstallFor(ctx, scope);
                if (registered != null)
                {
                    registered.Locations.Add(_bundle.Folder(ctx, scope));
                    found.Add(registered);
                    continue;
                }

                if (!_bundle.Exists(ctx, scope)) continue;

                InstallScope captured = scope;
                var files = new ExistingInstall
                {
                    Key = "bundle-" + scope.ToString().ToLowerInvariant(),
                    ProductName = Name,
                    Version = _bundle.InstalledVersion(ctx, scope),
                    InstalledBy = "NwTaggerSetup 1.0 or copied files",
                    Scope = scope,
                    NeedsElevation = scope == InstallScope.AllUsers,
                    Remove = delegate (SetupContext c) { _bundle.Remove(c, captured); }
                };
                files.Locations.Add(_bundle.Folder(ctx, scope));
                found.Add(files);
            }

            return found;
        }

        public override void Install(SetupContext ctx, InstallScope scope, IList<HostTarget> targets)
        {
            _bundle.Install(ctx, scope, targets, Version);
        }

        public override void RemoveFiles(SetupContext ctx, InstallScope scope)
        {
            _bundle.Remove(ctx, scope);
        }
    }
}
