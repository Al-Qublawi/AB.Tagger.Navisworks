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
    ///
    /// It also watches the dock panel while tagging is running. Navisworks gives
    /// a dock pane an X and tells nobody when it is pressed - the pane simply
    /// stops being visible - so closing the panel used to leave the tagger still
    /// holding every left-click in the 3D view with nothing on screen saying so.
    /// A closed panel now turns tagging off, exactly like the DISABLE button.
    /// </summary>
    public static class ToolController
    {
        /// <summary>Plugin name and developer id, joined the way Navisworks ids them.</summary>
        public const string ToolPluginId = "NwTagger.Tool.ABHM";

        public const string PanePluginId = "NwTagger.Pane.ABHM";

        /// <summary>Watches the panel while tagging is on. UI thread, like the panel itself.</summary>
        private static System.Windows.Forms.Timer _paneWatchdog;

        /// <summary>
        /// True once the panel has been seen open while tagging was running. Until
        /// then a hidden panel means nothing: tagging can be started from the
        /// ribbon with the panel never opened, and that must keep working.
        /// </summary>
        private static bool _paneSeenVisible;

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

            StartPaneWatchdog();

            message = optionsMessage;
            return true;
        }

        /// <summary>
        /// Starts watching the dock panel, so closing it stops tagging. Harmless
        /// to call twice.
        /// </summary>
        private static void StartPaneWatchdog()
        {
            _paneSeenVisible = IsPaneVisible();

            if (_paneWatchdog != null)
            {
                _paneWatchdog.Start();
                return;
            }

            try
            {
                _paneWatchdog = new System.Windows.Forms.Timer();
                _paneWatchdog.Interval = 400;
                _paneWatchdog.Tick += delegate { PaneWatchdogTick(); };
                _paneWatchdog.Start();
            }
            catch
            {
                // No timer means no watchdog; the DISABLE button still works.
                _paneWatchdog = null;
            }
        }

        private static void StopPaneWatchdog()
        {
            if (_paneWatchdog == null) return;

            try { _paneWatchdog.Stop(); }
            catch { /* going away anyway */ }
        }

        /// <summary>
        /// Turns tagging off once the panel it belongs to is no longer on screen.
        /// </summary>
        private static void PaneWatchdogTick()
        {
            try
            {
                // Tagging already stopped some other way - nothing left to watch.
                if (!IsActive)
                {
                    TaggerSettings.Current.IsEnabled = false;
                    StopPaneWatchdog();
                    return;
                }

                if (IsPaneVisible())
                {
                    _paneSeenVisible = true;
                    return;
                }

                // Never open, so the panel is not what is driving this session.
                if (!_paneSeenVisible) return;

                Disable();
            }
            catch
            {
                // A watchdog must never throw into the Navisworks message loop.
            }
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
                _paneSeenVisible = false;
                StopPaneWatchdog();
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
