using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Navisworks.Api;
using NwApplication = Autodesk.Navisworks.Api.Application;

namespace NwTagger.Core
{
    /// <summary>
    /// Tags a few elements by itself and checks that the markup is really in the
    /// document afterwards - the check a person cannot do by eye, because markup
    /// that is on screen is not necessarily markup that was stored.
    ///
    /// Why it exists: tags could vanish from a viewpoint all at once, leaving the
    /// viewpoint sitting there empty. Nothing about that is visible while you are
    /// looking at the viewpoint that still has the markup on the view, so every
    /// check here leaves the viewpoint and comes back before reading the markup -
    /// bouncing off a scratch viewpoint, exactly as a user clicking around would.
    ///
    /// It puts the document back as it found it: the viewpoints it created are
    /// removed, the camera, the view's markup, the selection, the tag list, the
    /// tag numbering and the tool state are all restored.
    ///
    /// Run it from Tool Add-ins - "AB Tagger - Self test". It needs a model open.
    ///
    /// Three environment variables make it usable for support and for testing a
    /// build without anyone at the keyboard:
    ///
    ///   ABTAGGER_SELFTEST=1         run it by itself, as soon as a model is open
    ///   ABTAGGER_SELFTEST_REPORT    where to write the report (default: Desktop)
    ///   ABTAGGER_SELFTEST_QUIET=1   no dialogs, just the report file
    /// </summary>
    public static class TaggerSelfTest
    {
        /// <summary>What the caller needs to show or log.</summary>
        public sealed class Result
        {
            public bool Passed { get; set; }
            public int Checks { get; set; }
            public int Failures { get; set; }
            public string Summary { get; set; }
            public string ReportPath { get; set; }
            public string Report { get; set; }
        }

        private const string ScratchName = "AB Tagger self test (scratch)";

        /// <summary>Set ABTAGGER_SELFTEST=1 to have it run on its own.</summary>
        private const string RunVariable = "ABTAGGER_SELFTEST";

        private static System.Windows.Forms.Timer _unattended;
        private static bool _unattendedDone;

        private static StringBuilder _log;
        private static int _checks;
        private static int _failures;

        /// <summary>
        /// Starts an unattended run if ABTAGGER_SELFTEST=1 is set, and does
        /// nothing at all otherwise.
        ///
        /// Navisworks has no model open when a plugin loads, and the test needs
        /// elements to tag, so it waits - on the message loop, not a thread, since
        /// every Navisworks call here has to be on the main thread - until a model
        /// and a 3D view are actually there.
        /// </summary>
        public static void StartUnattendedIfRequested()
        {
            try
            {
                if (!string.Equals(Environment.GetEnvironmentVariable(RunVariable), "1", StringComparison.Ordinal))
                    return;

                if (_unattended != null || _unattendedDone) return;

                _unattended = new System.Windows.Forms.Timer();
                _unattended.Interval = 1500;
                _unattended.Tick += delegate { UnattendedTick(); };
                _unattended.Start();
            }
            catch
            {
                // A diagnostic hook must never stop the add-in loading.
                _unattended = null;
            }
        }

        private static void UnattendedTick()
        {
            try
            {
                Document doc = NwApplication.ActiveDocument;

                if (doc == null || doc.IsClear) return;
                if (doc.ActiveView == null || doc.ActiveView.Width < 200) return;

                // Give the model a moment to finish drawing before tagging it.
                if (_unattended != null)
                {
                    _unattended.Stop();
                    _unattended.Dispose();
                    _unattended = null;
                }

                _unattendedDone = true;

                Run(true);
            }
            catch
            {
                _unattendedDone = true;
            }
        }

        /// <summary>
        /// Runs every case. <paramref name="includeExport"/> also writes a
        /// workbook and a .csv to the temporary folder and checks their rows,
        /// which means photographing the viewpoints - a few seconds more.
        /// </summary>
        public static Result Run(bool includeExport)
        {
            _log = new StringBuilder();
            _checks = 0;
            _failures = 0;

            Document doc = NwApplication.ActiveDocument;

            if (doc == null || doc.IsClear)
            {
                return new Result
                {
                    Passed = false,
                    Summary = "Open a model first - the self test needs elements to tag."
                };
            }

            View view = doc.ActiveView;

            if (view == null || view.Width < 200 || view.Height < 200)
            {
                return new Result
                {
                    Passed = false,
                    Summary = "The 3D view is too small (or missing) to place test tags in."
                };
            }

            Header(doc, view);

            // ---- everything that has to go back afterwards ----
            Viewpoint originalCamera = TryCreateCameraCopy(doc);
            string originalRedlines = SafeGetRedlines(view);
            int tagsBefore = TagStore.Current.Count;
            int numberBefore = ViewpointService.Current.NextTagNumber;
            bool toolWasActive = ToolController.IsActive;
            bool paneWasVisible = ToolController.IsPaneVisible();
            ModelItemCollection originalSelection = SafeSelection(doc);
            int viewpointsBefore = CountViewpoints(doc);

            List<Guid> created = new List<Guid>();

            try
            {
                // A viewpoint to bounce off, so "leave and come back" is a real
                // round trip through the document rather than a no-op.
                Guid scratch = CreateScratchViewpoint(doc);
                if (scratch != Guid.Empty) created.Add(scratch);

                ViewpointService.Current.Reset();

                List<TagRecord> placed = CaseManyTagsOneViewpoint(doc, view, scratch, created);
                CaseTagAfterComingBack(doc, view, scratch, created, placed);
                CaseCameraMovedStartsNewViewpoint(doc, view, scratch, created, placed);

                if (includeExport) CaseExportRows(doc, placed);

                CasePanelCloseStopsTagging(doc);
            }
            catch (Exception ex)
            {
                Fail("self test crashed", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Cleanup(doc, view, created, originalCamera, originalRedlines,
                        tagsBefore, numberBefore, originalSelection, toolWasActive, paneWasVisible);

                int viewpointsAfter = CountViewpoints(doc);
                Check(viewpointsAfter == viewpointsBefore,
                      "document left with the viewpoints it started with",
                      viewpointsBefore + " before, " + viewpointsAfter + " after");
            }

            return Finish();
        }

        // ------------------------------------------------------------- cases

        /// <summary>
        /// Several tags from one camera belong in one viewpoint, and every one of
        /// them has to still be in that viewpoint afterwards. This is the case
        /// that used to fail: the viewpoint stayed, its markup did not.
        /// </summary>
        private static List<TagRecord> CaseManyTagsOneViewpoint(
            Document doc, View view, Guid scratch, List<Guid> created)
        {
            Section("Five tags from one camera");

            List<TagRecord> placed = new List<TagRecord>();
            List<ModelItem> targets = PickTargets(doc, view, 5);

            Check(targets.Count >= 2, "found elements to tag", targets.Count + " element(s)");
            if (targets.Count == 0) return placed;

            Guid viewpoint = Guid.Empty;
            int expected = 0;

            for (int i = 0; i < targets.Count; i++)
            {
                TagRecord record = PlaceTag(doc, view, targets[i], i);

                if (record == null)
                {
                    Fail("tag " + (i + 1) + " was written", "TagService returned nothing");
                    continue;
                }

                placed.Add(record);
                expected += MarkupCount(record);

                if (viewpoint == Guid.Empty) viewpoint = record.ViewpointGuid;

                Line("  tag " + (i + 1) + ": " + record.TagName
                   + "  viewpoint \"" + record.ViewpointName + "\""
                   + (record.ReusedViewpoint ? " (reused)" : " (new)")
                   + "  markup expected " + expected);

                Check(string.IsNullOrEmpty(record.Warning),
                      "tag " + (i + 1) + " stored without having to be repaired",
                      record.Warning);

                Check(record.ViewpointGuid == viewpoint,
                      "tag " + (i + 1) + " went into the same viewpoint",
                      "expected " + Short(viewpoint) + ", got " + Short(record.ViewpointGuid));

                // Read it back out of the document, not off the view.
                int stored = StoredMarkupCount(doc, view, scratch, record.ViewpointGuid);

                Check(stored < 0 || stored >= expected,
                      "viewpoint still holds every tag after tag " + (i + 1),
                      "stored " + stored + ", expected at least " + expected);
            }

            if (viewpoint != Guid.Empty && !created.Contains(viewpoint)) created.Add(viewpoint);

            // And once more after doing nothing else: markup that only lives on
            // the view would have gone by now.
            if (viewpoint != Guid.Empty)
            {
                int stored = StoredMarkupCount(doc, view, scratch, viewpoint);
                Check(stored < 0 || stored >= expected,
                      "markup survives leaving the viewpoint and coming back",
                      "stored " + stored + ", expected at least " + expected);
            }

            return placed;
        }

        /// <summary>
        /// Clicking away to another viewpoint and back, then tagging again. The
        /// tag belongs in the same viewpoint - the camera has not changed - and
        /// none of the earlier markup may be lost on the way.
        ///
        /// This is the ordinary way of working that made the whole viewpoint go
        /// empty, because by now the document has copied this viewpoint several
        /// times over.
        /// </summary>
        private static void CaseTagAfterComingBack(
            Document doc, View view, Guid scratch, List<Guid> created, List<TagRecord> placed)
        {
            Section("Another tag after leaving the viewpoint and coming back");

            if (placed.Count == 0)
            {
                Line("  skipped - nothing was tagged in the first case");
                return;
            }

            Guid viewpoint = placed[0].ViewpointGuid;

            int expected = 0;
            foreach (TagRecord record in placed) expected += MarkupCount(record);

            // Away, and back - which also puts the camera exactly where the
            // viewpoint was captured from.
            if (scratch != Guid.Empty) Select(doc, scratch);
            Select(doc, viewpoint);

            List<ModelItem> targets = PickTargets(doc, view, 1);

            if (targets.Count == 0)
            {
                Line("  skipped - nothing in view to tag from here");
                return;
            }

            TagRecord again = PlaceTag(doc, view, targets[0], 2);

            if (again == null)
            {
                Fail("tag after coming back was written", "TagService returned nothing");
                return;
            }

            if (!created.Contains(again.ViewpointGuid)) created.Add(again.ViewpointGuid);

            expected += MarkupCount(again);

            Line("  tag: " + again.TagName + "  viewpoint \"" + again.ViewpointName + "\""
               + (again.ReusedViewpoint ? " (reused)" : " (new)"));

            Check(string.IsNullOrEmpty(again.Warning),
                  "the tag stored without having to be repaired", again.Warning);

            Check(again.ViewpointGuid == viewpoint,
                  "it went back into the same viewpoint",
                  "expected " + Short(viewpoint) + ", got " + Short(again.ViewpointGuid));

            int stored = StoredMarkupCount(doc, view, scratch, viewpoint);

            Check(stored < 0 || stored >= expected,
                  "every tag in the viewpoint is still stored",
                  "stored " + stored + ", expected at least " + expected);

            placed.Add(again);
        }

        /// <summary>Moving the camera has to start a viewpoint of its own, and leave the first one alone.</summary>
        private static void CaseCameraMovedStartsNewViewpoint(
            Document doc, View view, Guid scratch, List<Guid> created, List<TagRecord> placed)
        {
            Section("A tag after moving the camera");

            if (placed.Count == 0)
            {
                Line("  skipped - nothing was tagged in the first case");
                return;
            }

            Guid first = placed[0].ViewpointGuid;
            int firstExpected = 0;
            foreach (TagRecord record in placed) firstExpected += MarkupCount(record);

            if (!TryOrbit(doc))
            {
                Line("  skipped - the camera could not be moved");
                return;
            }

            List<ModelItem> targets = PickTargets(doc, view, 1);

            if (targets.Count == 0)
            {
                Line("  skipped - nothing in view to tag from here");
                return;
            }

            TagRecord moved = PlaceTag(doc, view, targets[0], 0);

            if (moved == null)
            {
                Fail("tag after moving was written", "TagService returned nothing");
                return;
            }

            if (!created.Contains(moved.ViewpointGuid)) created.Add(moved.ViewpointGuid);

            Line("  tag: " + moved.TagName + "  viewpoint \"" + moved.ViewpointName + "\"");

            Check(moved.ViewpointGuid != first,
                  "a moved camera starts a new viewpoint",
                  "first " + Short(first) + ", now " + Short(moved.ViewpointGuid));

            int storedNew = StoredMarkupCount(doc, view, scratch, moved.ViewpointGuid);
            Check(storedNew < 0 || storedNew >= MarkupCount(moved),
                  "the new viewpoint holds its own markup",
                  "stored " + storedNew + ", expected at least " + MarkupCount(moved));

            int storedFirst = StoredMarkupCount(doc, view, scratch, first);
            Check(storedFirst < 0 || storedFirst >= firstExpected,
                  "the earlier viewpoint kept its markup",
                  "stored " + storedFirst + ", expected at least " + firstExpected);
        }

        /// <summary>
        /// The export writes one row per viewpoint. Tags that share a viewpoint
        /// share its row and its photo, instead of repeating the same picture once
        /// per tag.
        /// </summary>
        private static void CaseExportRows(Document doc, List<TagRecord> placed)
        {
            Section("Export rows");

            List<TagRecord> records = TagStore.Current.Snapshot();
            List<TagRecord> mine = new List<TagRecord>();

            foreach (TagRecord record in records)
            {
                foreach (TagRecord one in placed)
                {
                    if (ReferenceEquals(record, one)) { mine.Add(record); break; }
                }
            }

            if (mine.Count < 2)
            {
                Line("  skipped - fewer than two tags were placed");
                return;
            }

            List<Guid> viewpoints = new List<Guid>();
            foreach (TagRecord record in mine)
                if (!viewpoints.Contains(record.ViewpointGuid)) viewpoints.Add(record.ViewpointGuid);

            Line("  " + mine.Count + " tag(s) in " + viewpoints.Count + " viewpoint(s)");

            Dictionary<Guid, byte[]> photos = new Dictionary<Guid, byte[]>();

            try { photos = ViewpointPhotoService.CapturePhotos(doc, mine, null); }
            catch (Exception ex) { Line("  photographing failed: " + ex.Message); }

            Line("  " + photos.Count + " photo(s) captured");

            string stem = Path.Combine(Path.GetTempPath(),
                "ABTagger-SelfTest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

            string xlsx = stem + ".xlsx";
            string csv = stem + ".csv";

            try
            {
                int rows = TagExporter.Export(xlsx, mine, photos);

                Check(rows == viewpoints.Count,
                      "workbook has one row per viewpoint",
                      "rows " + rows + ", viewpoints " + viewpoints.Count);

                Check(File.Exists(xlsx) && new FileInfo(xlsx).Length > 0,
                      "workbook was written", xlsx);

                int sheetRows, images;
                if (ReadWorkbook(xlsx, out sheetRows, out images))
                {
                    Check(sheetRows == rows + 1,
                          "worksheet holds the header and one row each",
                          "rows in sheet " + sheetRows + ", expected " + (rows + 1));

                    Check(images == photos.Count,
                          "each photo is embedded once",
                          "images " + images + ", photos " + photos.Count);
                }
                else
                {
                    Fail("workbook could be read back", "the .xlsx did not parse");
                }

                int csvRows = TagExporter.Export(csv, mine, null);

                Check(csvRows == viewpoints.Count,
                      "csv has one row per viewpoint",
                      "rows " + csvRows + ", viewpoints " + viewpoints.Count);

                int csvLines = CountCsvRows(csv);
                Check(csvLines == csvRows + 1,
                      "csv file holds the header and one line each",
                      "lines " + csvLines + ", expected " + (csvRows + 1));
            }
            catch (Exception ex)
            {
                Fail("export ran", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                SafeDelete(xlsx);
                SafeDelete(csv);
            }
        }

        /// <summary>
        /// Closing the panel has to stop tagging. Navisworks says nothing when the
        /// X is pressed, so this hides the pane the same way and waits for the
        /// watchdog to notice.
        /// </summary>
        private static void CasePanelCloseStopsTagging(Document doc)
        {
            Section("Closing the panel stops tagging");

            try
            {
                ToolController.SetPaneVisible(true);
                Pump(300);

                if (!ToolController.IsPaneVisible())
                {
                    Line("  skipped - the panel would not open");
                    return;
                }

                string message;
                bool enabled = ToolController.Enable(out message);

                Check(enabled && ToolController.IsActive, "tagging started", message);
                if (!ToolController.IsActive) return;

                // This is what the X does: the pane stops being visible.
                ToolController.SetPaneVisible(false);

                bool off = WaitUntil(delegate { return !ToolController.IsActive; }, 4000);

                Check(off, "tagging stopped when the panel closed",
                      off ? null : "still active four seconds later");

                Check(!TaggerSettings.Current.IsEnabled,
                      "the panel's own enabled flag went with it", null);
            }
            catch (Exception ex)
            {
                Fail("panel close case ran", ex.GetType().Name + ": " + ex.Message);
            }
        }

        // -------------------------------------------------------- tag placing

        /// <summary>
        /// Places one tag on an element without a mouse: the anchor is where the
        /// element projects onto the screen, and the text goes at a spread-out
        /// offset from it so the tags do not sit on top of each other.
        /// </summary>
        private static TagRecord PlaceTag(Document doc, View view, ModelItem item, int index)
        {
            int anchorX, anchorY;
            if (!TryProject(view, item, out anchorX, out anchorY)) return null;

            Point3D point = Center(item);
            PendingTag pending = TagService.BeginTag(view, item, point, anchorX, anchorY);

            if (pending == null || pending.Lines == null || pending.Lines.Count == 0) return null;

            // Spread the text blocks around the middle of the view.
            int stepX = view.Width / 6;
            int stepY = view.Height / 6;

            int textX = Clamp(view.Width / 2 + stepX * ((index % 3) - 1), 20, view.Width - 40);
            int textY = Clamp(view.Height / 2 + stepY * ((index % 2) - 1), 20, view.Height - 40);

            return TagService.CompleteTag(view, pending, textX, textY);
        }

        /// <summary>Elements with geometry that can be seen from here, spread across the view.</summary>
        private static List<ModelItem> PickTargets(Document doc, View view, int wanted)
        {
            List<ModelItem> targets = new List<ModelItem>();
            List<int[]> taken = new List<int[]>();

            int scanned = 0;

            try
            {
                foreach (ModelItem item in doc.Models.RootItemDescendantsAndSelf)
                {
                    // Big models would take all day; the first few thousand
                    // visible elements are plenty.
                    if (++scanned > 20000 || targets.Count >= wanted) break;

                    if (item == null || !item.HasGeometry || item.IsHidden) continue;

                    int x, y;
                    if (!TryProject(view, item, out x, out y)) continue;

                    bool tooClose = false;

                    foreach (int[] other in taken)
                    {
                        if (Math.Abs(other[0] - x) < 60 && Math.Abs(other[1] - y) < 60) { tooClose = true; break; }
                    }

                    if (tooClose) continue;

                    taken.Add(new int[] { x, y });
                    targets.Add(item);
                }
            }
            catch
            {
                // Whatever was found is still usable.
            }

            return targets;
        }

        private static bool TryProject(View view, ModelItem item, out int x, out int y)
        {
            x = 0;
            y = 0;

            try
            {
                Point3D centre = Center(item);
                if (centre == null) return false;

                ProjectionResult projected = view.ProjectPoint(centre, false, false);
                if (projected == null) return false;

                // Keep well inside the view so the text block has room.
                int margin = 60;

                if (projected.X < margin || projected.X > view.Width - margin) return false;
                if (projected.Y < margin || projected.Y > view.Height - margin) return false;

                x = projected.X;
                y = projected.Y;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Point3D Center(ModelItem item)
        {
            try
            {
                BoundingBox3D box = item.BoundingBox();
                return box == null ? null : box.Center;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>One arrow plus one redline per line of text.</summary>
        private static int MarkupCount(TagRecord record)
        {
            if (record == null || record.Lines == null) return 0;

            int lines = 0;
            foreach (string line in record.Lines) if (!string.IsNullOrEmpty(line)) lines++;

            return lines + 1;
        }

        // ------------------------------------------------- reading storage back

        /// <summary>
        /// How much markup the *document* holds for a viewpoint. It leaves the
        /// viewpoint and comes back first, so nothing that is merely still on the
        /// view can be mistaken for something that was stored.
        /// </summary>
        private static int StoredMarkupCount(Document doc, View view, Guid scratch, Guid viewpoint)
        {
            try
            {
                if (scratch != Guid.Empty) Select(doc, scratch);
                Select(doc, viewpoint);

                SavedItem item = doc.SavedViewpoints.ResolveGuid(viewpoint);
                if (item == null) return -1;

                return MarkupBackend.Count(doc, view, item);
            }
            catch
            {
                return -1;
            }
        }

        private static void Select(Document doc, Guid viewpoint)
        {
            try
            {
                SavedItem item = doc.SavedViewpoints.ResolveGuid(viewpoint);
                if (item != null) doc.SavedViewpoints.CurrentSavedViewpoint = item;
                Pump(120);
            }
            catch
            {
                // Best effort.
            }
        }

        private static Guid CreateScratchViewpoint(Document doc)
        {
            try
            {
                Guid guid = Guid.NewGuid();

                SavedViewpoint scratch = new SavedViewpoint(doc.CurrentViewpoint.CreateCopy());
                try { scratch.Guid = guid; } catch { }
                scratch.DisplayName = ScratchName;

                using (Transaction transaction = doc.BeginTransaction("AB Tagger self test"))
                {
                    doc.SavedViewpoints.AddCopy(scratch);
                    transaction.Commit();
                }

                if (doc.SavedViewpoints.ResolveGuid(guid) != null) return guid;

                // The copy was given a different guid - find it by name.
                foreach (SavedItem item in AllViewpoints(doc))
                    if (string.Equals(item.DisplayName, ScratchName, StringComparison.Ordinal)) return item.Guid;

                return Guid.Empty;
            }
            catch
            {
                return Guid.Empty;
            }
        }

        // ----------------------------------------------------------- workbook

        /// <summary>Rows in the worksheet and images in the package.</summary>
        private static bool ReadWorkbook(string path, out int rows, out int images)
        {
            rows = 0;
            images = 0;

            try
            {
                using (FileStream stream = File.OpenRead(path))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                        if (entry.FullName.StartsWith("xl/media/", StringComparison.OrdinalIgnoreCase)) images++;

                    ZipArchiveEntry sheet = archive.GetEntry("xl/worksheets/sheet1.xml");
                    if (sheet == null) return false;

                    string xml;
                    using (StreamReader reader = new StreamReader(sheet.Open())) xml = reader.ReadToEnd();

                    rows = Regex.Matches(xml, "<row ").Count;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int CountCsvRows(string path)
        {
            try
            {
                int count = 0;

                foreach (string line in File.ReadAllLines(path))
                    if (!string.IsNullOrEmpty(line.Trim())) count++;

                return count;
            }
            catch
            {
                return -1;
            }
        }

        // ------------------------------------------------------------ cleanup

        private static void Cleanup(
            Document doc, View view, List<Guid> created,
            Viewpoint originalCamera, string originalRedlines,
            int tagsBefore, int numberBefore, ModelItemCollection originalSelection,
            bool toolWasActive, bool paneWasVisible)
        {
            Section("Putting everything back");

            int removed = 0;

            foreach (Guid guid in created)
            {
                try
                {
                    SavedItem item = doc.SavedViewpoints.ResolveGuid(guid);
                    if (item == null) continue;

                    using (Transaction transaction = doc.BeginTransaction("AB Tagger self test cleanup"))
                    {
                        doc.SavedViewpoints.Remove(item);
                        transaction.Commit();
                    }

                    removed++;
                }
                catch
                {
                    // Reported below by the viewpoint count check.
                }
            }

            // Anything left with our scratch name, whatever its guid turned out to be.
            try
            {
                foreach (SavedItem item in AllViewpoints(doc))
                {
                    if (!string.Equals(item.DisplayName, ScratchName, StringComparison.Ordinal)) continue;

                    using (Transaction transaction = doc.BeginTransaction("AB Tagger self test cleanup"))
                    {
                        doc.SavedViewpoints.Remove(item);
                        transaction.Commit();
                    }

                    removed++;
                }
            }
            catch
            {
            }

            Line("  removed " + removed + " viewpoint(s) the test created");

            TagStore.Current.TrimTo(tagsBefore);
            ViewpointService.Current.Reset();
            ViewpointService.Current.NextTagNumber = numberBefore;

            if (originalCamera != null)
            {
                try { doc.CurrentViewpoint.CopyFrom(originalCamera); }
                catch { }
            }

            if (originalRedlines != null)
            {
                try { view.TrySetRedlines(originalRedlines); }
                catch { }
            }

            try
            {
                if (originalSelection != null) doc.CurrentSelection.CopyFrom(originalSelection);
            }
            catch
            {
            }

            try
            {
                if (toolWasActive)
                {
                    string message;
                    ToolController.Enable(out message);
                }
                else
                {
                    ToolController.Disable();
                }

                ToolController.SetPaneVisible(paneWasVisible);
            }
            catch
            {
            }

            Line("  tag list back to " + TagStore.Current.Count + " record(s), next tag number " + numberBefore);
        }

        // -------------------------------------------------------------- report

        private static void Header(Document doc, View view)
        {
            Line("AB Tagger self test");
            Line(new string('=', 60));
            Line("when          " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            Line("add-in        " + Version());
            Line("markup route  " + ViewpointService.BackendDescription);

            try { Line("navisworks    " + NwApplication.Version.ApiMajor + "." + NwApplication.Version.ApiMinor + "  " + NwApplication.Version.RuntimeProductName); }
            catch { Line("navisworks    (version not available)"); }

            try { Line("model         " + doc.Title); }
            catch { }

            Line("view          " + view.Width + " x " + view.Height + " px");
            Line("viewpoints    " + CountViewpoints(doc) + " before the test");
            Line(string.Empty);
        }

        private static string Version()
        {
            try
            {
                return typeof(TaggerSelfTest).Assembly.GetName().Version.ToString();
            }
            catch
            {
                return "(unknown)";
            }
        }

        private static void Section(string title)
        {
            Line(string.Empty);
            Line("-- " + title);
        }

        private static void Line(string text)
        {
            _log.AppendLine(text ?? string.Empty);
        }

        private static void Check(bool ok, string what, string detail)
        {
            _checks++;
            if (!ok) _failures++;

            string line = (ok ? "  PASS  " : "  FAIL  ") + what;
            if (!ok && !string.IsNullOrEmpty(detail)) line += "   [" + detail + "]";
            else if (ok && !string.IsNullOrEmpty(detail)) line += "   (" + detail + ")";

            Line(line);
        }

        private static void Fail(string what, string detail)
        {
            Check(false, what, detail);
        }

        private static Result Finish()
        {
            Line(string.Empty);
            Line(new string('=', 60));

            string summary = _failures == 0
                ? "All " + _checks + " checks passed."
                : _failures + " of " + _checks + " checks FAILED.";

            Line(summary);

            string report = _log.ToString();
            string path = WriteReport(report);

            return new Result
            {
                Passed = _failures == 0,
                Checks = _checks,
                Failures = _failures,
                Summary = summary,
                Report = report,
                ReportPath = path
            };
        }

        private static string WriteReport(string report)
        {
            string path = null;

            try
            {
                path = Environment.GetEnvironmentVariable("ABTAGGER_SELFTEST_REPORT");

                if (string.IsNullOrEmpty(path))
                {
                    path = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        "ABTagger-SelfTest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
                }

                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);

                File.WriteAllText(path, report, new UTF8Encoding(true));

                return path;
            }
            catch
            {
                return null;
            }
        }

        // -------------------------------------------------------------- odds

        private static IEnumerable<SavedItem> AllViewpoints(Document doc)
        {
            List<SavedItem> all = new List<SavedItem>();
            Collect(doc.SavedViewpoints.RootItem, all);
            return all;
        }

        private static void Collect(GroupItem group, List<SavedItem> into)
        {
            if (group == null) return;

            foreach (SavedItem child in group.Children)
            {
                into.Add(child);

                GroupItem childGroup = child as GroupItem;
                if (childGroup != null) Collect(childGroup, into);
            }
        }

        private static int CountViewpoints(Document doc)
        {
            try
            {
                int count = 0;
                foreach (SavedItem item in AllViewpoints(doc)) if (!item.IsGroup) count++;
                return count;
            }
            catch
            {
                return -1;
            }
        }

        private static Viewpoint TryCreateCameraCopy(Document doc)
        {
            try { return doc.CurrentViewpoint.CreateCopy(); }
            catch { return null; }
        }

        private static string SafeGetRedlines(View view)
        {
            try { return view.GetRedlines(); }
            catch { return null; }
        }

        private static ModelItemCollection SafeSelection(Document doc)
        {
            try
            {
                ModelItemCollection selection = new ModelItemCollection();
                selection.CopyFrom(doc.CurrentSelection.SelectedItems);
                return selection;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Turns the camera a little, so the next tag cannot share the viewpoint.</summary>
        private static bool TryOrbit(Document doc)
        {
            try
            {
                Viewpoint moved = doc.CurrentViewpoint.CreateCopy();

                // Sliding the camera sideways is enough - any real move starts a
                // new viewpoint - and it keeps the model on screen, which turning
                // would not. The step is scaled to what the view covers, so it
                // works on a model in millimetres or in feet.
                double step = moved.HeightField > 0 ? moved.HeightField * 0.05 : 1.0;

                Point3D from = moved.Position;
                moved.Position = new Point3D(from.X + step, from.Y, from.Z);

                doc.CurrentViewpoint.CopyFrom(moved);
                Pump(200);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static int Clamp(int value, int lo, int hi)
        {
            if (value < lo) return lo;
            if (value > hi) return hi;
            return value;
        }

        private static string Short(Guid guid)
        {
            return guid == Guid.Empty ? "(none)" : guid.ToString("N").Substring(0, 8);
        }

        /// <summary>
        /// Lets Navisworks paint and its timers tick. The watchdog that stops
        /// tagging when the panel closes is one of those timers, so the test has
        /// to give the message loop a turn.
        /// </summary>
        private static void Pump(int milliseconds)
        {
            int waited = 0;

            while (waited < milliseconds)
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(20);
                waited += 20;
            }
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMilliseconds)
        {
            int waited = 0;

            while (waited < timeoutMilliseconds)
            {
                if (condition()) return true;

                Pump(100);
                waited += 100;
            }

            return condition();
        }
    }
}
