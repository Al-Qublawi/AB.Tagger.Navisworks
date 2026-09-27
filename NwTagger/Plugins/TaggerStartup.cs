using Autodesk.Navisworks.Api.Plugins;
using ABAdvTools;
using ABAdvTools.Navisworks;
using NwTagger.Core;

namespace NwTagger.Plugins
{
    /// <summary>
    /// Joins AB Tagger to the AB Adv Tools suite when Navisworks starts: registers it for the
    /// shared About dialog, merges its ribbon tab into the shared one, and checks GitHub for a
    /// newer release.
    ///
    /// An EventWatcherPlugin because that is the only plugin kind Navisworks loads at startup;
    /// the ribbon handler loads lazily, far too late to merge tabs or announce an update.
    /// </summary>
    [Plugin("NwTagger.Startup", "ABHM",
        DisplayName = "AB Tagger startup",
        ToolTip = "Adds AB Tagger to the AB Adv Tools tab and checks for new releases")]
    public sealed class TaggerStartup : EventWatcherPlugin
    {
        public override void OnLoaded()
        {
            NavisworksAdvTools.Start(Product);

            // Only does something when ABTAGGER_SELFTEST=1 is set - see
            // TaggerSelfTest. It is how a build gets tested in a real Navisworks
            // without a person clicking through it.
            TaggerSelfTest.StartUnattendedIfRequested();
        }

        public override void OnUnloading()
        {
            NavisworksAdvTools.Stop();
        }

        /// <summary>AB Tagger as the suite knows it. Releases come from its own repository.</summary>
        internal static AdvToolsProduct Product
        {
            get
            {
                return new AdvToolsProduct(
                    "Tagger",
                    "AB Tagger",
                    "Element tags from Quick Properties, as native redline markup",
                    AdvToolsHost.Navisworks,
                    "AB.Tagger.Navisworks",
                    typeof(TaggerStartup).Assembly)
                {
                    Details = delegate
                    {
                        return "Click an element, then where the text goes: its Quick Properties become native " +
                               "redline markup in a saved viewpoint. Open the panel from AB Tagger on the ribbon or " +
                               "View > Windows > Element Tagger; export the tags to Excel from the Tags panel.";
                    }
                };
            }
        }
    }
}
