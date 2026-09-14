using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Interop;
using Autodesk.Navisworks.Api.Plugins;
using NwTagger.Core;
using View = Autodesk.Navisworks.Api.View;
using Application = Autodesk.Navisworks.Api.Application;

namespace NwTaggerProbe
{
    /// <summary>
    /// Proves out the whole 2027 write path before the tagger is refactored
    /// around it. It exercises exactly the sequence the 2027 build would use:
    ///
    ///   1. LcOpRedline.ScreenToCameraSpace  - pixels to camera space
    ///   2. RedlineJson.Serialize            - the format captured from 2027
    ///   3. View.SetRedlines                 - put markup on the live view
    ///   4. AddCopy + ReplaceFromCurrentView - persist it into a saved viewpoint
    ///   5. read it back                     - confirm it actually stuck
    ///
    /// Step 4 is the open question. In 2026 markup is added straight to the
    /// viewpoint's redline list; that API is gone in 2027, so the markup has to
    /// travel via the live view. If the read-back is empty, this approach does
    /// not work and 2027 needs rethinking.
    ///
    /// This one DOES modify the model - it adds a viewpoint named
    /// "AB Tagger write test". It asks first, and the viewpoint is easy to
    /// delete afterwards.
    /// </summary>
    [Plugin("ABTagger.WriteTest", "ABHM",
        DisplayName = "AB Tagger - Test Markup Write (2027 probe)",
        ToolTip = "Writes test markup via the JSON route and checks it persists into a saved viewpoint.")]
    [AddInPlugin(AddInLocation.AddIn)]
    public sealed class WriteTestPlugin : AddInPlugin
    {
        private const string TestViewpointName = "AB Tagger write test";

        public override int Execute(params string[] parameters)
        {
            Document doc = Application.ActiveDocument;

            if (doc == null || doc.IsClear)
            {
                MessageBox.Show("Open a model first.", "AB Tagger probe",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            View view = doc.ActiveView;
            if (view == null) return 0;

            DialogResult go = MessageBox.Show(
                "This writes test markup and adds a viewpoint called\n"
                    + "\"" + TestViewpointName + "\" to this model.\n\n"
                    + "It is the only way to confirm 2027 support is possible.\n"
                    + "Delete the viewpoint afterwards, and do not save the file\n"
                    + "if you would rather it left no trace.\n\n"
                    + "Continue?",
                "AB Tagger probe", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (go != DialogResult.Yes) return 0;

            StringBuilder log = new StringBuilder();
            bool success = false;

            try
            {
                success = RunTest(doc, view, log);
            }
            catch (Exception ex)
            {
                log.AppendLine();
                log.AppendLine("EXCEPTION: " + ex);
            }

            MessageBox.Show(log.ToString(), "AB Tagger write test - " + (success ? "PASSED" : "FAILED"),
                MessageBoxButtons.OK, success ? MessageBoxIcon.Information : MessageBoxIcon.Error);

            return 0;
        }

        private static bool RunTest(Document doc, View view, StringBuilder log)
        {
            int width = view.Width;
            int height = view.Height;

            log.AppendLine("View size: " + width + " x " + height);

            // --- 1. pixels -> camera space, the same call the 2026 build uses ---
            LcOaViewer viewer = view.Viewer;
            if (viewer == null)
            {
                log.AppendLine("FAILED: view.Viewer was null.");
                return false;
            }

            int textX = (int)(width * 0.25);
            int textY = (int)(height * 0.25);
            int anchorX = (int)(width * 0.60);
            int anchorY = (int)(height * 0.60);

            Point2D textPoint = LcOpRedline.ScreenToCameraSpace(viewer, textX, textY);
            Point2D anchorPoint = LcOpRedline.ScreenToCameraSpace(viewer, anchorX, anchorY);

            log.AppendLine("ScreenToCameraSpace works:");
            log.AppendLine("   text   px(" + textX + "," + textY + ")  -> camera("
                + textPoint.X.ToString("0.######", CultureInfo.InvariantCulture) + ","
                + textPoint.Y.ToString("0.######", CultureInfo.InvariantCulture) + ")");
            log.AppendLine("   anchor px(" + anchorX + "," + anchorY + ")  -> camera("
                + anchorPoint.X.ToString("0.######", CultureInfo.InvariantCulture) + ","
                + anchorPoint.Y.ToString("0.######", CultureInfo.InvariantCulture) + ")");

            // --- 2. build and serialise ---
            List<MarkupItem> items = new List<MarkupItem>();

            items.Add(new MarkupArrow
            {
                StartX = textPoint.X,
                StartY = textPoint.Y,
                EndX = anchorPoint.X,
                EndY = anchorPoint.Y,
                Thickness = 2,
                ColorR = 1,
                ColorG = 0,
                ColorB = 0
            });

            items.Add(new MarkupText
            {
                OriginX = textPoint.X,
                OriginY = textPoint.Y,
                Text = "AB TAGGER 2027 TEST",
                ColorR = 1,
                ColorG = 0,
                ColorB = 0
            });

            string json = RedlineJson.Serialize(items);
            log.AppendLine();
            log.AppendLine("Serialised (" + json.Length + " chars).");

            // --- 3. put it on the live view ---
            if (!view.TrySetRedlines(json))
            {
                log.AppendLine("FAILED at step 3: TrySetRedlines rejected the generated JSON.");
                log.AppendLine("The generator does not match what Navisworks expects.");
                return false;
            }

            log.AppendLine("SetRedlines accepted the generated JSON.");

            string liveBack = view.GetRedlines();
            bool liveHasText = liveBack != null && liveBack.IndexOf("AB TAGGER 2027 TEST", StringComparison.Ordinal) >= 0;
            log.AppendLine("Live view read-back contains the text: " + (liveHasText ? "YES" : "NO"));

            if (!liveHasText)
            {
                log.AppendLine("FAILED: markup did not stick to the live view.");
                return false;
            }

            // --- 4. persist into a saved viewpoint ---
            SavedViewpoint saved = new SavedViewpoint(doc.CurrentViewpoint.CreateCopy());
            saved.DisplayName = TestViewpointName;

            using (Transaction transaction = doc.BeginTransaction("AB Tagger write test"))
            {
                doc.SavedViewpoints.AddCopy(saved);
                transaction.Commit();
            }

            SavedViewpoint stored = FindByName(doc.SavedViewpoints.RootItem, TestViewpointName);

            if (stored == null)
            {
                log.AppendLine("FAILED at step 4: the viewpoint was not found after AddCopy.");
                return false;
            }

            // AddCopy only carries the camera. ReplaceFromCurrentView is what
            // pulls the live view's markup into the stored viewpoint.
            using (Transaction transaction = doc.BeginTransaction("AB Tagger write test - capture"))
            {
                doc.SavedViewpoints.ReplaceFromCurrentView(stored);
                transaction.Commit();
            }

            log.AppendLine("ReplaceFromCurrentView completed.");

            // --- 5. read it back from the saved viewpoint ---
            // Clear the live view first, so what comes back can only have come
            // from the stored viewpoint rather than lingering on screen.
            view.TrySetRedlines(RedlineJson.Empty);

            stored = FindByName(doc.SavedViewpoints.RootItem, TestViewpointName);
            doc.SavedViewpoints.CurrentSavedViewpoint = stored;

            string storedBack = view.GetRedlines();
            bool persisted = storedBack != null
                && storedBack.IndexOf("AB TAGGER 2027 TEST", StringComparison.Ordinal) >= 0;

            log.AppendLine();
            log.AppendLine("Saved viewpoint read-back contains the text: " + (persisted ? "YES" : "NO"));
            log.AppendLine();
            log.AppendLine(persisted
                ? "PASSED - the full 2027 write path works. Markup can be generated,\n"
                  + "applied and persisted into a viewpoint, so the tagger can support 2027."
                : "FAILED - markup reached the live view but did not persist into the\n"
                  + "saved viewpoint. 2027 needs a different persistence route.");

            log.AppendLine();
            log.AppendLine("Delete the \"" + TestViewpointName + "\" viewpoint when you are done.");

            return persisted;
        }

        private static SavedViewpoint FindByName(GroupItem group, string name)
        {
            if (group == null) return null;

            foreach (SavedItem item in group.Children)
            {
                SavedViewpoint viewpoint = item as SavedViewpoint;

                if (viewpoint != null &&
                    string.Equals(viewpoint.DisplayName, name, StringComparison.Ordinal))
                    return viewpoint;

                GroupItem child = item as GroupItem;
                if (child == null) continue;

                SavedViewpoint found = FindByName(child, name);
                if (found != null) return found;
            }

            return null;
        }

        public override CommandState CanExecute()
        {
            CommandState state = new CommandState(true);
            state.IsEnabled = Application.ActiveDocument != null && !Application.ActiveDocument.IsClear;
            return state;
        }
    }
}
