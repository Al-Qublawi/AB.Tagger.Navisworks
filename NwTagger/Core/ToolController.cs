using System;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;
using NwTagger.Plugins;

namespace NwTagger.Core
{
    /// <summary>
    /// Turns the tagging tool on and off.
    ///
    /// A ToolPlugin becomes active by handing it to Document.Tool via
    /// SetCustomToolPlugin; deactivating is just switching back to the
    /// standard Select tool.
    /// </summary>
    public static class ToolController
    {
        /// <summary>Plugin name and developer id, joined the way Navisworks ids them.</summary>
        public const string ToolPluginId = "NwTagger.Tool.ABHM";

        public const string PanePluginId = "NwTagger.Pane.ABHM";

        /// <summary>True when our tool currently owns input in the 3D view.</summary>
        public static bool IsActive
        {
            get
            {
                try
                {
                    Document doc = Application.ActiveDocument;
                    if (doc == null) return false;

                    if (doc.Tool.Value != Tool.CustomToolPlugin) return false;

                    return string.Equals(doc.Tool.CustomToolPluginId, ToolPluginId, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Activates the tool. Returns false with a reason when it cannot start.
        /// </summary>
        public static bool Enable(out string message)
        {
            message = null;

            Document doc = Application.ActiveDocument;
            if (doc == null || doc.IsClear)
            {
                message = "Open a model before enabling the tagger.";
                return false;
            }

            ToolPluginRecord record = Application.Plugins.FindPlugin(ToolPluginId) as ToolPluginRecord;
            if (record == null)
            {
                message = "Could not find the tagging tool plugin (" + ToolPluginId + ").";
                return false;
            }

            ToolPlugin tool = record.LoadedPlugin ?? record.LoadPlugin();
            if (tool == null)
            {
                message = "The tagging tool plugin failed to load.";
                return false;
            }

            // Push the current text settings into the Navisworks redline defaults
            // so the markup we create looks the way the panel says it will.
            string optionsMessage;
            GlobalOptionsBridge.ApplyTextSettings(TaggerSettings.Current, out optionsMessage);

            doc.Tool.SetCustomToolPlugin(tool);
            TaggerSettings.Current.IsEnabled = true;

            message = optionsMessage;
            return true;
        }

        /// <summary>Returns Navisworks to the standard Select tool.</summary>
        public static void Disable()
        {
            try
            {
                ToolPluginRecord record = Application.Plugins.FindPlugin(ToolPluginId) as ToolPluginRecord;

                if (record != null)
                {
                    TaggerToolPlugin tool = record.LoadedPlugin as TaggerToolPlugin;
                    if (tool != null) tool.CancelPending();
                }

                Document doc = Application.ActiveDocument;
                if (doc != null && doc.Tool.Value == Tool.CustomToolPlugin)
                    doc.Tool.Value = Tool.Select;
            }
            catch
            {
                // Best effort - the flag below is what the UI reads.
            }
            finally
            {
                TaggerSettings.Current.IsEnabled = false;
            }
        }

        /// <summary>Shows or hides the dock pane.</summary>
        public static void SetPaneVisible(bool visible)
        {
            DockPanePluginRecord record = Application.Plugins.FindPlugin(PanePluginId) as DockPanePluginRecord;
            if (record == null) return;

            DockPanePlugin pane = record.LoadedPlugin ?? record.LoadPlugin();
            if (pane == null) return;

            pane.Visible = visible;
        }

        /// <summary>Current visibility of the dock pane.</summary>
        public static bool IsPaneVisible()
        {
            try
            {
                DockPanePluginRecord record = Application.Plugins.FindPlugin(PanePluginId) as DockPanePluginRecord;
                if (record == null) return false;

                DockPanePlugin pane = record.LoadedPlugin;
                return pane != null && pane.Visible;
            }
            catch
            {
                return false;
            }
        }
    }
}
