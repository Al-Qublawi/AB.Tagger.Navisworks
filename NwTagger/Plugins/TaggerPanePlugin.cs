using System.Windows.Forms;
using Autodesk.Navisworks.Api.Plugins;
using NwTagger.UI;

namespace NwTagger.Plugins
{
    /// <summary>
    /// The dockable settings panel. Navisworks owns the docking, we just supply
    /// a WinForms control.
    /// </summary>
    [Plugin("NwTagger.Pane", "ABHM",
        DisplayName = "Element Tagger",
        ToolTip = "Tag settings, enable/disable, and tag export.")]
    [DockPanePlugin(320, 620,
        AutoScroll = true,
        MinimumWidth = 290,
        MinimumHeight = 420,
        FixedSize = false)]
    public sealed class TaggerPanePlugin : DockPanePlugin
    {
        public override Control CreateControlPane()
        {
            TaggerPaneControl control = new TaggerPaneControl();
            control.Dock = DockStyle.Fill;
            control.CreateControl();
            return control;
        }

        public override void DestroyControlPane(Control pane)
        {
            if (pane == null) return;
            pane.Dispose();
        }
    }
}
