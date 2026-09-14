using System;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;
using ABAdvTools.Navisworks;
using NwTagger.Core;

namespace NwTagger.Plugins
{
    /// <summary>
    /// AB Tagger's panels on the shared "AB Adv Tools" ribbon tab.
    ///
    /// The layout lives in NwTagger.xaml, which Navisworks reads as a loose file
    /// beside the plugin DLL (not an embedded resource), and the button images
    /// come from the Icon / LargeIcon names below, resolved against the Images
    /// folder next to the DLL.
    ///
    /// The tab id, and the About / Check for Updates / LinkedIn commands, are the suite's
    /// (NavisworksAdvTools); every AB plugin declares the same ones, and the kit merges
    /// their tabs into one at startup.
    /// </summary>
    [Plugin("NwTagger.Ribbon", "ABHM",
        DisplayName = "AB Tagger",
        ToolTip = "Element tagging with Quick Properties")]
    [RibbonLayout("NwTagger.xaml")]
    [RibbonTab(NavisworksAdvTools.RibbonTabId, DisplayName = NavisworksAdvTools.RibbonTabTitle, LoadForCanExecute = true)]
    [Command("ID_ABT_Enable",
        DisplayName = "Enable",
        Icon = "enable_16.ico", LargeIcon = "enable_32.ico",
        CanToggle = true,
        LoadForCanExecute = true,
        ToolTip = "Start tagging: click an element, then click where the text goes.")]
    [Command("ID_ABT_Disable",
        DisplayName = "Disable",
        Icon = "disable_16.ico", LargeIcon = "disable_32.ico",
        LoadForCanExecute = true,
        ToolTip = "Stop tagging and return to the Select tool.")]
    [Command("ID_ABT_Panel",
        DisplayName = "AB Tagger",
        Icon = "logo_16.ico", LargeIcon = "logo_32.ico",
        CanToggle = true,
        LoadForCanExecute = true,
        ToolTip = "Show or hide the AB Tagger panel.")]
    [Command("ID_ABT_Export",
        DisplayName = "Export to Excel",
        Icon = "export_16.ico", LargeIcon = "export_32.ico",
        LoadForCanExecute = true,
        ToolTip = "Export this session's tags, with a photo of each viewpoint.")]
    [Command("ID_ABT_Clear",
        DisplayName = "Clear tag list",
        Icon = "clear_16.ico", LargeIcon = "clear_32.ico",
        LoadForCanExecute = true,
        ToolTip = "Empty the session tag list. Saved viewpoints are not touched.")]
    [Command(NavisworksAdvTools.AboutCommandId,
        DisplayName = NavisworksAdvTools.AboutText,
        Icon = NavisworksAdvTools.AboutIcon, LargeIcon = NavisworksAdvTools.AboutLargeIcon,
        ToolTip = NavisworksAdvTools.AboutToolTip)]
    [Command(NavisworksAdvTools.UpdatesCommandId,
        DisplayName = NavisworksAdvTools.UpdatesText,
        Icon = NavisworksAdvTools.UpdatesIcon, LargeIcon = NavisworksAdvTools.UpdatesLargeIcon,
        ToolTip = NavisworksAdvTools.UpdatesToolTip)]
    [Command(NavisworksAdvTools.LinkedInCommandId,
        DisplayName = NavisworksAdvTools.LinkedInText,
        Icon = NavisworksAdvTools.LinkedInIcon, LargeIcon = NavisworksAdvTools.LinkedInLargeIcon,
        ToolTip = NavisworksAdvTools.LinkedInToolTip)]
    public sealed class TaggerRibbonPlugin : CommandHandlerPlugin
    {
        public override int ExecuteCommand(string commandId, params string[] parameters)
        {
            try
            {
                if (NavisworksAdvTools.TryExecuteSharedCommand(commandId)) return 0;

                switch (commandId)
                {
                    case "ID_ABT_Enable":
                        EnableTagging();
                        break;

                    case "ID_ABT_Disable":
                        ToolController.Disable();
                        break;

                    case "ID_ABT_Panel":
                        ToolController.SetPaneVisible(!ToolController.IsPaneVisible());
                        break;

                    case "ID_ABT_Export":
                        TagExportRunner.Run(null, ShowIfProblem);
                        break;

                    case "ID_ABT_Clear":
                        ClearTags();
                        break;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "AB Tagger", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return 0;
        }

        /// <summary>
        /// Mirrors the panel's ENABLE button, including surfacing anything
        /// Navisworks refused when the text settings were applied.
        /// </summary>
        private static void EnableTagging()
        {
            string message;

            if (!ToolController.Enable(out message))
            {
                MessageBox.Show(message ?? "Could not enable the tagger.",
                    "AB Tagger", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Make sure the panel is up, since that is where status is reported.
            if (!ToolController.IsPaneVisible()) ToolController.SetPaneVisible(true);

            if (!string.IsNullOrEmpty(message))
                MessageBox.Show(message, "AB Tagger", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void ClearTags()
        {
            if (TagStore.Current.Count == 0)
            {
                MessageBox.Show("There are no tags in the list.",
                    "AB Tagger", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult answer = MessageBox.Show(
                "Clear the session tag list?\n\nThis only clears the list. "
                    + "Saved viewpoints and their markup are not touched.",
                "AB Tagger", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (answer != DialogResult.Yes) return;

            TagStore.Current.Clear();
            ViewpointService.Current.Reset();
        }

        /// <summary>
        /// The ribbon has nowhere to show running status, so only surface the
        /// messages that need an answer - the panel shows the rest.
        /// </summary>
        private static void ShowIfProblem(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            if (message.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("no tags", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                MessageBox.Show(message, "AB Tagger", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        public override CommandState CanExecuteCommand(string commandId)
        {
            CommandState state = new CommandState(true);

            Document document = Autodesk.Navisworks.Api.Application.ActiveDocument;
            bool hasDocument = document != null && !document.IsClear;
            bool tagging = ToolController.IsActive;

            switch (commandId)
            {
                case "ID_ABT_Enable":
                    state.IsEnabled = hasDocument && !tagging;
                    state.IsChecked = tagging;
                    break;

                case "ID_ABT_Disable":
                    state.IsEnabled = tagging;
                    break;

                case "ID_ABT_Panel":
                    state.IsEnabled = true;
                    state.IsChecked = ToolController.IsPaneVisible();
                    break;

                case "ID_ABT_Export":
                case "ID_ABT_Clear":
                    state.IsEnabled = TagStore.Current.Count > 0;
                    break;

                default:
                    state.IsEnabled = true;
                    break;
            }

            return state;
        }

        /// <summary>The tab is always available; individual buttons gate themselves.</summary>
        public override bool CanExecuteRibbonTab(string ribbonTabId)
        {
            return true;
        }
    }
}
