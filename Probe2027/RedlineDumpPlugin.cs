using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;

// System.Windows.Forms also defines View and Application; the Navisworks ones
// are what we mean throughout this file.
using View = Autodesk.Navisworks.Api.View;
using Application = Autodesk.Navisworks.Api.Application;

namespace NwTaggerProbe
{
    /// <summary>
    /// Diagnostic tool for Navisworks 2027.
    ///
    /// 2027 made LcOpRedlineList internal and removed
    /// SavedViewpoint.EditRedlines(), so the tagger cannot attach markup to a
    /// viewpoint the way it does in 2026. What survives is a string round-trip:
    ///
    ///     View.GetRedlines()            -> string
    ///     View.TrySetRedlines(string)   -> bool
    ///
    /// The format of that string is undocumented. This plugin captures it from
    /// markup drawn by hand, walks every saved viewpoint collecting samples, and
    /// checks that the string Navisworks produces is accepted back. With that
    /// sample the tagger can generate the same form directly and support 2027.
    ///
    /// It writes a report to the Desktop and changes nothing in the model.
    /// </summary>
    [Plugin("ABTagger.RedlineProbe", "ABHM",
        DisplayName = "AB Tagger - Dump Redlines (2027 probe)",
        ToolTip = "Writes the redline format of every saved viewpoint to a file on the Desktop.")]
    [AddInPlugin(AddInLocation.AddIn)]
    public sealed class RedlineDumpPlugin : AddInPlugin
    {
        /// <summary>Keeps a huge model's viewpoint list from making this slow.</summary>
        private const int MaxViewpoints = 40;

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

            if (view == null)
            {
                MessageBox.Show("No active 3D view.", "AB Tagger probe",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            StringBuilder report = new StringBuilder();
            int samplesWithMarkup = 0;

            try
            {
                WriteHeader(report, doc, view);

                // The live view first - this is whatever is on screen right now.
                string live = SafeGetRedlines(view);
                WriteSample(report, "CURRENT VIEW (before walking viewpoints)", null, live, view);
                if (HasContent(live)) samplesWithMarkup++;

                samplesWithMarkup += WalkSavedViewpoints(doc, view, report);

                RoundTripTest(view, report);
            }
            catch (Exception ex)
            {
                report.AppendLine();
                report.AppendLine("!! The probe stopped early: " + ex);
            }

            string path = WriteReport(report.ToString());

            ShowResult(path, samplesWithMarkup);

            return 0;
        }

        // ------------------------------------------------------------ capture

        private static int WalkSavedViewpoints(Document doc, View view, StringBuilder report)
        {
            List<SavedViewpoint> viewpoints = new List<SavedViewpoint>();
            CollectViewpoints(doc.SavedViewpoints.RootItem, viewpoints);

            report.AppendLine();
            report.AppendLine("Saved viewpoints found: " + viewpoints.Count.ToString(CultureInfo.InvariantCulture));

            if (viewpoints.Count == 0)
            {
                report.AppendLine("(No saved viewpoints. Draw some markup with the Review tools, save a");
                report.AppendLine(" viewpoint, and run this again.)");
                return 0;
            }

            // Put the user's view back exactly as it was.
            SavedItem originalCurrent = null;
            Viewpoint originalCamera = null;

            try { originalCurrent = doc.SavedViewpoints.CurrentSavedViewpoint; } catch { }
            try { originalCamera = doc.CurrentViewpoint.CreateCopy(); } catch { }

            int withMarkup = 0;

            try
            {
                int limit = Math.Min(viewpoints.Count, MaxViewpoints);

                for (int i = 0; i < limit; i++)
                {
                    SavedViewpoint saved = viewpoints[i];
                    string name = SafeName(saved);

                    try
                    {
                        // Redlines only surface while their viewpoint is current.
                        doc.SavedViewpoints.CurrentSavedViewpoint = saved;

                        string redlines = SafeGetRedlines(view);
                        WriteSample(report, "SAVED VIEWPOINT [" + i + "] " + name, saved, redlines, view);

                        if (HasContent(redlines)) withMarkup++;
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine();
                        report.AppendLine("--- SAVED VIEWPOINT [" + i + "] " + name + " : FAILED");
                        report.AppendLine("    " + ex.Message);
                    }
                }

                if (viewpoints.Count > limit)
                {
                    report.AppendLine();
                    report.AppendLine("(" + (viewpoints.Count - limit) + " further viewpoints were not sampled.)");
                }
            }
            finally
            {
                try { if (originalCurrent != null) doc.SavedViewpoints.CurrentSavedViewpoint = originalCurrent; }
                catch { }

                try { if (originalCamera != null) doc.CurrentViewpoint.CopyFrom(originalCamera); }
                catch { }
            }

            return withMarkup;
        }

        private static void CollectViewpoints(GroupItem group, List<SavedViewpoint> into)
        {
            if (group == null) return;

            foreach (SavedItem item in group.Children)
            {
                SavedViewpoint viewpoint = item as SavedViewpoint;
                if (viewpoint != null) into.Add(viewpoint);

                GroupItem child = item as GroupItem;
                if (child != null) CollectViewpoints(child, into);
            }
        }

        private static string SafeGetRedlines(View view)
        {
            try
            {
                return view.GetRedlines();
            }
            catch (Exception ex)
            {
                return "<<GetRedlines threw: " + ex.GetType().Name + ": " + ex.Message + ">>";
            }
        }

        /// <summary>
        /// Confirms Navisworks accepts back the exact string it produced. If this
        /// fails, generating the format is not viable and 2027 needs a different
        /// approach entirely.
        /// </summary>
        private static void RoundTripTest(View view, StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine(Divider);
            report.AppendLine("ROUND-TRIP TEST");
            report.AppendLine(Divider);

            try
            {
                string captured = view.GetRedlines();

                if (!HasContent(captured))
                {
                    report.AppendLine("Skipped: the current view has no markup to round-trip.");
                    report.AppendLine("Draw a text and an arrow, then run the probe again.");
                    return;
                }

                bool accepted = view.TrySetRedlines(captured);

                report.AppendLine("TrySetRedlines(its own output) -> " + (accepted ? "ACCEPTED" : "REJECTED"));
                report.AppendLine(accepted
                    ? "The write path works, so generating this format is viable."
                    : "The write path refused its own output - generating it will not work.");
            }
            catch (Exception ex)
            {
                report.AppendLine("Round-trip threw: " + ex);
            }
        }

        // ------------------------------------------------------------- report

        private const string Divider = "================================================================";

        private static void WriteHeader(StringBuilder report, Document doc, View view)
        {
            report.AppendLine(Divider);
            report.AppendLine("AB Tagger - Navisworks redline format probe");
            report.AppendLine(Divider);
            report.AppendLine("Generated       : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

            try
            {
                report.AppendLine("Product         : " + Application.Version.RuntimeProductName);
                report.AppendLine("Runtime         : " + Application.Version.Runtime);
                report.AppendLine("API version     : " + Application.Version.ApiMajor + "." + Application.Version.ApiMinor);
            }
            catch (Exception ex)
            {
                report.AppendLine("Version         : unavailable (" + ex.Message + ")");
            }

            try
            {
                report.AppendLine("Model           : " + Path.GetFileName(doc.FileName ?? string.Empty));
            }
            catch { }

            // View size matters: redline coordinates are camera space, and the
            // mapping depends on the viewport aspect.
            try
            {
                report.AppendLine("View size (px)  : " + view.Width + " x " + view.Height);
            }
            catch { }

            report.AppendLine();
            report.AppendLine("WHAT THIS IS FOR");
            report.AppendLine("  Navisworks 2027 removed the API the tagger uses to write markup into a");
            report.AppendLine("  viewpoint. The remaining route is View.GetRedlines()/SetRedlines(string),");
            report.AppendLine("  whose format is undocumented. These samples are what a generator for that");
            report.AppendLine("  format has to reproduce.");
            report.AppendLine();
            report.AppendLine("BEST SAMPLE TO CAPTURE");
            report.AppendLine("  1. Review tab -> Text, and type a short recognisable string such as ABC123.");
            report.AppendLine("  2. Review tab -> Arrow, and drag one arrow.");
            report.AppendLine("  3. Save the viewpoint, then run this probe.");
            report.AppendLine("  A short, distinctive text string makes it obvious where the text lives in");
            report.AppendLine("  the encoding, and one arrow keeps the geometry easy to read.");
        }

        private static void WriteSample(StringBuilder report, string title, SavedViewpoint saved, string redlines, View view)
        {
            report.AppendLine();
            report.AppendLine(Divider);
            report.AppendLine(title);
            report.AppendLine(Divider);

            if (saved != null)
            {
                try
                {
                    Viewpoint vp = saved.Viewpoint;
                    if (vp != null) report.AppendLine("Camera          : " + vp.GetCamera());
                }
                catch (Exception ex)
                {
                    report.AppendLine("Camera          : unavailable (" + ex.Message + ")");
                }
            }

            try
            {
                report.AppendLine("View size (px)  : " + view.Width + " x " + view.Height);
            }
            catch { }

            if (redlines == null)
            {
                report.AppendLine("Redlines        : null");
                return;
            }

            report.AppendLine("Length (chars)  : " + redlines.Length.ToString(CultureInfo.InvariantCulture));
            report.AppendLine("Looks like      : " + DescribeFormat(redlines));
            report.AppendLine("Has content     : " + (HasContent(redlines) ? "yes" : "no - empty or placeholder"));
            report.AppendLine();
            report.AppendLine("----- RAW -----");
            report.AppendLine(redlines);
            report.AppendLine("----- END RAW -----");

            // Base64 as well, so nothing is lost if the raw text contains control
            // characters that do not survive being copied around.
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(redlines);
                report.AppendLine();
                report.AppendLine("----- BASE64 (UTF-8) -----");
                report.AppendLine(Convert.ToBase64String(bytes, Base64FormattingOptions.InsertLineBreaks));
                report.AppendLine("----- END BASE64 -----");
            }
            catch { }
        }

        /// <summary>A first guess at the encoding, to steer the decoding work.</summary>
        private static string DescribeFormat(string value)
        {
            if (string.IsNullOrEmpty(value)) return "empty string";

            string trimmed = value.TrimStart();

            if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)) return "XML (with declaration)";
            if (trimmed.StartsWith("<", StringComparison.Ordinal)) return "XML / markup";
            if (trimmed.StartsWith("{", StringComparison.Ordinal)) return "JSON object";
            if (trimmed.StartsWith("[", StringComparison.Ordinal)) return "JSON array";

            bool printable = true;
            foreach (char c in value)
            {
                if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') { printable = false; break; }
            }

            return printable ? "plain text / delimited" : "binary or encoded (contains control characters)";
        }

        private static bool HasContent(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (value.StartsWith("<<GetRedlines threw", StringComparison.Ordinal)) return false;

            // An empty redline document still serialises to a short wrapper.
            return value.Trim().Length > 40;
        }

        private static string SafeName(SavedItem item)
        {
            try { return item.DisplayName ?? "(unnamed)"; }
            catch { return "(unnamed)"; }
        }

        private static string WriteReport(string content)
        {
            string fileName = "ABTagger-Redline-Dump-"
                + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt";

            string path;

            try
            {
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), fileName);
                File.WriteAllText(path, content, new UTF8Encoding(true));
            }
            catch
            {
                // Desktop may be redirected or read-only.
                path = Path.Combine(Path.GetTempPath(), fileName);
                File.WriteAllText(path, content, new UTF8Encoding(true));
            }

            return path;
        }

        private static void ShowResult(string path, int samplesWithMarkup)
        {
            string message;
            MessageBoxIcon icon;

            if (samplesWithMarkup == 0)
            {
                message = "The report was written, but no markup was found in any viewpoint.\n\n"
                        + "To capture a useful sample:\n"
                        + "  1. Review tab -> Text, type something like ABC123\n"
                        + "  2. Review tab -> Arrow, drag one arrow\n"
                        + "  3. Save the viewpoint\n"
                        + "  4. Run this again\n\n"
                        + path;
                icon = MessageBoxIcon.Warning;
            }
            else
            {
                message = "Captured markup from " + samplesWithMarkup + " view(s).\n\n"
                        + "Report written to:\n" + path + "\n\n"
                        + "Send that file back and 2027 support can be built from it.\n\n"
                        + "Open the containing folder now?";
                icon = MessageBoxIcon.Information;
            }

            if (samplesWithMarkup == 0)
            {
                MessageBox.Show(message, "AB Tagger probe", MessageBoxButtons.OK, icon);
                return;
            }

            DialogResult answer = MessageBox.Show(message, "AB Tagger probe", MessageBoxButtons.YesNo, icon);
            if (answer != DialogResult.Yes) return;

            try
            {
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch
            {
                // Opening Explorer is a convenience, not a requirement.
            }
        }

        public override CommandState CanExecute()
        {
            CommandState state = new CommandState(true);
            state.IsEnabled = Application.ActiveDocument != null && !Application.ActiveDocument.IsClear;
            return state;
        }
    }
}
