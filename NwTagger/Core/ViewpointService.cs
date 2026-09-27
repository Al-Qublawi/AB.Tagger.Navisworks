using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Autodesk.Navisworks.Api;

namespace NwTagger.Core
{
    /// <summary>
    /// Snapshot of the camera, used to decide whether the user has moved
    /// between two tags. Two tags placed from the same camera share a viewpoint.
    /// </summary>
    internal sealed class CameraKey
    {
        private const double PositionTolerance = 1e-6;
        private const double RotationTolerance = 1e-9;
        private const double FieldTolerance = 1e-9;

        /// <summary>
        /// How far the field of view may differ and still count as "the same
        /// view" when matching a saved viewpoint. Restoring a viewpoint into a
        /// window of a different shape adjusts it slightly, so an exact match
        /// would never happen and tags would never land in the viewpoint the
        /// user is actually looking at.
        /// </summary>
        private const double FieldRatioTolerance = 0.02;

        private readonly double _x, _y, _z;
        private readonly double _a, _b, _c, _d;
        private readonly double _heightField;
        private readonly ViewpointProjection _projection;

        private CameraKey(Viewpoint vp)
        {
            Point3D position = vp.Position;
            _x = position.X;
            _y = position.Y;
            _z = position.Z;

            Rotation3D rotation = vp.Rotation;
            _a = rotation.A;
            _b = rotation.B;
            _c = rotation.C;
            _d = rotation.D;

            _heightField = vp.HeightField;
            _projection = vp.Projection;
        }

        internal static CameraKey FromCurrent(Document doc)
        {
            try
            {
                Viewpoint vp = doc.CurrentViewpoint.ToViewpoint();
                return vp == null ? null : new CameraKey(vp);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The camera of a viewpoint that is already in the document.</summary>
        internal static CameraKey From(Viewpoint viewpoint)
        {
            try
            {
                return viewpoint == null ? null : new CameraKey(viewpoint);
            }
            catch
            {
                return null;
            }
        }

        internal bool Matches(CameraKey other)
        {
            return Matches(other, false);
        }

        /// <summary>
        /// Same camera, allowing the small field-of-view difference that
        /// restoring a saved viewpoint into this window introduces.
        /// </summary>
        internal bool MatchesRestoredViewpoint(CameraKey other)
        {
            return Matches(other, true);
        }

        private bool Matches(CameraKey other, bool allowFieldDrift)
        {
            if (other == null) return false;

            bool sameField = allowFieldDrift
                ? NearRatio(_heightField, other._heightField, FieldRatioTolerance)
                : Near(_heightField, other._heightField, FieldTolerance);

            return Near(_x, other._x, PositionTolerance)
                && Near(_y, other._y, PositionTolerance)
                && Near(_z, other._z, PositionTolerance)
                && Near(_a, other._a, RotationTolerance)
                && Near(_b, other._b, RotationTolerance)
                && Near(_c, other._c, RotationTolerance)
                && Near(_d, other._d, RotationTolerance)
                && sameField
                && _projection == other._projection;
        }

        private static bool Near(double a, double b, double tolerance)
        {
            return Math.Abs(a - b) <= tolerance;
        }

        private static bool NearRatio(double a, double b, double tolerance)
        {
            double scale = Math.Max(Math.Abs(a), Math.Abs(b));
            if (scale <= 1e-9) return true;

            return Math.Abs(a - b) / scale <= tolerance;
        }
    }

    /// <summary>Outcome of writing a tag into a saved viewpoint.</summary>
    public sealed class ViewpointResult
    {
        /// <summary>This tag's own sequential name, e.g. "Tag 03".</summary>
        public string TagName { get; set; }

        /// <summary>
        /// Name of the viewpoint holding it: the user's own viewpoint when the
        /// tag went into one of theirs, otherwise this add-in's own name for it -
        /// "Tag 03", or a range like "Tag 01-03" when several tags were placed
        /// without moving the camera.
        /// </summary>
        public string ViewpointName { get; set; }

        public bool Reused { get; set; }

        /// <summary>
        /// False when the tag went into a viewpoint that was already in the
        /// document - one the user made, or one from an earlier session. Those
        /// are never renamed and never have their own markup disturbed.
        /// </summary>
        public bool OwnViewpoint { get; set; }

        /// <summary>Identifies the viewpoint so the exporter can photograph it.</summary>
        public Guid ViewpointGuid { get; set; }

        /// <summary>
        /// Set when the markup did not survive the first write and something had
        /// to be done about it; null when the tag was stored normally. The tool
        /// puts this on the panel's status line rather than letting a lost tag
        /// pass unnoticed.
        /// </summary>
        public string Warning { get; set; }
    }

    /// <summary>
    /// Owns the saved viewpoint the tags are being written into.
    ///
    /// There are two kinds of target, and the difference matters:
    ///
    ///   * **A viewpoint the user is already on.** If a saved viewpoint is
    ///     current and the camera is still its own, the tag goes into *that*
    ///     viewpoint: its name is left alone, and the markup it already had -
    ///     including redlines drawn by hand - is kept. This is what people
    ///     expect when they open a viewpoint and start tagging.
    ///   * **A viewpoint of our own.** Otherwise a "Tag 03" viewpoint is created
    ///     for the camera, and further tags from that same camera widen it to
    ///     "Tag 03-05".
    ///
    /// What it keeps between tags is the *camera* and the list of markup, never a
    /// viewpoint object the document has seen. Every write builds a brand new
    /// SavedViewpoint - from the camera for our own viewpoints, or from a fresh
    /// copy of how the user's viewpoint looked when it was adopted.
    ///
    /// That is deliberate, and it is the fix for tags disappearing. A viewpoint
    /// object that has been copied into the document once hands back a redline
    /// list that no longer belongs to it: Clear and Add still appear to work, the
    /// writes go nowhere, and the next copy pushed to the document carries no
    /// markup at all - so every tag in that viewpoint vanishes at once and the
    /// viewpoint sits there empty. A fresh object cannot get into that state.
    ///
    /// Each write is then checked by reading the markup back out of the document,
    /// and done again if it did not land.
    /// </summary>
    public sealed class ViewpointService
    {
        private static readonly ViewpointService _current = new ViewpointService();
        public static ViewpointService Current { get { return _current; } }

        private ViewpointService() { }

        /// <summary>Matches names this add-in generates, e.g. "Tag 07" or "Tag 07-09".</summary>
        private static readonly Regex TagNamePattern =
            new Regex(@"^Tag\s+(\d+)(?:\s*-\s*(\d+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>True when a name looks like one this add-in generated.</summary>
        internal static bool IsGeneratedName(string name)
        {
            return !string.IsNullOrEmpty(name) && TagNamePattern.IsMatch(name.Trim());
        }

        /// <summary>The camera the active viewpoint was captured from.</summary>
        private Viewpoint _camera;

        private CameraKey _cameraKey;
        private Guid _guid;
        private bool _inDocument;
        private string _viewpointName;

        /// <summary>The name before the last widening, used to re-find the item.</summary>
        private string _previousViewpointName;

        /// <summary>
        /// False while tags are going into a viewpoint that was already in the
        /// document. Those are never renamed, and what they already held is kept.
        /// </summary>
        private bool _ownViewpoint = true;

        /// <summary>
        /// A detached copy of an adopted viewpoint exactly as it was found, kept
        /// so every write can start from it again. Copying it per write is what
        /// makes a repeated write harmless: the user's markup appears once, ours
        /// appears once, however many times the write is attempted.
        /// </summary>
        private SavedViewpoint _baseline;

        /// <summary>The same thing for releases that keep markup on the view, as JSON.</summary>
        private string _baselineJson;

        /// <summary>How much markup the adopted viewpoint already had.</summary>
        private int _baselineMarkup;

        /// <summary>
        /// Every markup item this add-in has added to the active viewpoint. Held
        /// here rather than read back from the document, so each write can
        /// rebuild the whole set.
        /// </summary>
        private readonly List<MarkupItem> _items = new List<MarkupItem>();

        private int _nextTagNumber = 1;
        private bool _numberingInitialised;

        private int _firstTagInViewpoint;
        private int _lastTagInViewpoint;

        /// <summary>Which storage route this build uses - named in the diagnostics report.</summary>
        public static string BackendDescription { get { return MarkupBackend.Description; } }

        /// <summary>Forgets the active viewpoint, so the next tag starts a new one.</summary>
        public void Reset()
        {
            _camera = null;
            _cameraKey = null;
            _guid = Guid.Empty;
            _inDocument = false;
            _viewpointName = null;
            _previousViewpointName = null;
            _ownViewpoint = true;
            _baseline = null;
            _baselineJson = null;
            _baselineMarkup = 0;
            _firstTagInViewpoint = 0;
            _lastTagInViewpoint = 0;
            _items.Clear();
        }

        /// <summary>Resets naming as well - used when the document changes.</summary>
        public void ResetAll()
        {
            Reset();
            _nextTagNumber = 1;
            _numberingInitialised = false;
        }

        /// <summary>
        /// The number the next tag will be given. The diagnostics put this back
        /// where it found it, so a self test does not consume tag numbers.
        /// </summary>
        internal int NextTagNumber
        {
            get { return _nextTagNumber; }
            set
            {
                _nextTagNumber = value < 1 ? 1 : value;
                _numberingInitialised = true;
            }
        }

        /// <summary>
        /// Adds redlines to the saved viewpoint for the current camera: the one
        /// the user is on if they are on one, otherwise a new one of our own.
        /// </summary>
        public ViewpointResult AddTag(
            Document doc,
            View view,
            IEnumerable<MarkupItem> markup,
            ModelItemCollection selection)
        {
            if (doc == null) throw new ArgumentNullException("doc");

            EnsureNumbering(doc);

            int tagNumber = _nextTagNumber++;
            string tagName = FormatTagName(tagNumber);

            CameraKey now = CameraKey.FromCurrent(doc);
            bool sameTarget = _inDocument && _camera != null && _cameraKey != null
                              && _cameraKey.Matches(now) && ResolveStored(doc) != null;

            // This tag's own markup is kept aside: if the shared viewpoint turns
            // out not to take it, the tag still gets a viewpoint of its own.
            List<MarkupItem> mine = new List<MarkupItem>();
            if (markup != null) mine.AddRange(markup);

            if (!sameTarget)
            {
                if (!TryAdoptCurrentViewpoint(doc, view, now))
                    StartNewViewpoint(doc, now, tagNumber);
            }
            else if (_ownViewpoint)
            {
                WidenName(tagNumber);
            }

            _items.AddRange(mine);

            string warning = null;

            if (!TryWrite(doc, view, selection))
            {
                // Attempt two, with another brand new viewpoint object. This is
                // what recovers when a write silently stored nothing.
                if (TryWrite(doc, view, selection))
                {
                    warning = "Navisworks dropped the markup on the first attempt - " + tagName
                            + " was written again into \"" + _viewpointName + "\".";
                }
                else
                {
                    // Give this tag a viewpoint of its own rather than lose it.
                    // Whatever is already stored in the old one stays there.
                    Reset();
                    StartNewViewpoint(doc, now, tagNumber);
                    _items.AddRange(mine);
                    sameTarget = false;

                    warning = TryWrite(doc, view, selection)
                        ? "The viewpoint would not take more markup, so " + tagName
                            + " was saved in a viewpoint of its own."
                        : "Navisworks would not store the markup for " + tagName + ".";
                }
            }

            return new ViewpointResult
            {
                TagName = tagName,
                ViewpointName = _viewpointName,
                Reused = sameTarget || !_ownViewpoint,
                OwnViewpoint = _ownViewpoint,
                ViewpointGuid = _guid,
                Warning = warning
            };
        }

        private static string FormatTagName(int number)
        {
            return "Tag " + number.ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>Widens the viewpoint name to cover every tag it now holds.</summary>
        private void WidenName(int tagNumber)
        {
            _previousViewpointName = _viewpointName;
            _lastTagInViewpoint = tagNumber;

            _viewpointName = _firstTagInViewpoint == _lastTagInViewpoint
                ? FormatTagName(_firstTagInViewpoint)
                : FormatTagName(_firstTagInViewpoint) + "-" + tagNumber.ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Takes over the viewpoint the user is already on, so the tag goes into
        /// it instead of a new one beside it.
        ///
        /// Two conditions, both necessary. There has to *be* a current saved
        /// viewpoint - Navisworks reports which one is selected - and the camera
        /// has to still be that viewpoint's own. Markup is stored in camera
        /// space, so writing it into a viewpoint the user has navigated away from
        /// would put the tag somewhere else entirely the next time that viewpoint
        /// is restored.
        /// </summary>
        private bool TryAdoptCurrentViewpoint(Document doc, View view, CameraKey now)
        {
            if (!TaggerSettings.Current.AddToCurrentViewpoint) return false;
            if (now == null) return false;

            SavedItem current;

            try { current = doc.SavedViewpoints.CurrentSavedViewpoint; }
            catch { return false; }

            SavedViewpoint viewpoint = current as SavedViewpoint;
            if (viewpoint == null || current.IsGroup) return false;

            CameraKey theirs;

            try { theirs = CameraKey.From(viewpoint.Viewpoint); }
            catch { return false; }

            if (theirs == null || !theirs.MatchesRestoredViewpoint(now)) return false;

            SavedViewpoint baseline = null;

            try { baseline = current.CreateCopy() as SavedViewpoint; }
            catch { baseline = null; }

            // Without a copy of how we found it, this release cannot put the
            // user's own markup back on each write - so leave their viewpoint
            // alone and make one of ours instead.
            if (baseline == null && MarkupBackend.MarkupTravelsInViewpoint) return false;

            Viewpoint camera;

            try { camera = doc.CurrentViewpoint.CreateCopy(); }
            catch { return false; }

            _camera = camera;
            _cameraKey = now;
            _guid = current.Guid;
            _inDocument = true;
            _ownViewpoint = false;
            _baseline = baseline;
            _baselineJson = MarkupBackend.CaptureBaseline(doc, view, current);
            _viewpointName = current.DisplayName;
            _previousViewpointName = null;
            _firstTagInViewpoint = 0;
            _lastTagInViewpoint = 0;
            _items.Clear();

            int already = MarkupBackend.Count(doc, view, current);
            _baselineMarkup = already > 0 ? already : 0;

            return true;
        }

        /// <summary>
        /// Starts numbering after any "Tag NN" viewpoints already in the document,
        /// so re-opening a model and tagging again does not restart at 01.
        /// </summary>
        private void EnsureNumbering(Document doc)
        {
            if (_numberingInitialised) return;
            _numberingInitialised = true;

            int highest = 0;

            try
            {
                foreach (SavedItem item in EnumerateAll(doc.SavedViewpoints.RootItem))
                {
                    if (item == null || string.IsNullOrEmpty(item.DisplayName)) continue;

                    Match match = TagNamePattern.Match(item.DisplayName.Trim());
                    if (!match.Success) continue;

                    // A range name "Tag 01-03" ends at the second number.
                    string last = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[1].Value;

                    int value;
                    if (int.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > highest)
                        highest = value;
                }
            }
            catch
            {
                // Fall back to starting at 1.
            }

            _nextTagNumber = highest + 1;
        }

        private static IEnumerable<SavedItem> EnumerateAll(GroupItem group)
        {
            if (group == null) yield break;

            foreach (SavedItem child in group.Children)
            {
                yield return child;

                GroupItem childGroup = child as GroupItem;
                if (childGroup == null) continue;

                foreach (SavedItem descendant in EnumerateAll(childGroup))
                    yield return descendant;
            }
        }

        private void StartNewViewpoint(Document doc, CameraKey camera, int tagNumber)
        {
            _camera = doc.CurrentViewpoint.CreateCopy();
            _cameraKey = camera;
            _guid = Guid.NewGuid();
            _inDocument = false;
            _ownViewpoint = true;
            _baseline = null;
            _baselineJson = null;
            _baselineMarkup = 0;

            // A new viewpoint starts with no markup of its own.
            _items.Clear();

            _firstTagInViewpoint = tagNumber;
            _lastTagInViewpoint = tagNumber;
            _viewpointName = FormatTagName(tagNumber);
            _previousViewpointName = null;
        }

        /// <summary>
        /// One complete attempt at writing the active viewpoint - markup and all -
        /// into the document. Returns false when the markup is not in the document
        /// afterwards, whatever the reason; the caller decides what to do then.
        /// </summary>
        private bool TryWrite(Document doc, View view, ModelItemCollection selection)
        {
            try
            {
                // Always a brand new object, never one the document has seen.
                SavedViewpoint fresh = BuildViewpointObject();
                if (fresh == null) return false;

                using (Transaction transaction = doc.BeginTransaction(_inDocument ? "Update tag viewpoint" : "Add tag viewpoint"))
                {
                    // Storing the selection alongside the camera makes the
                    // viewpoint restore the tagged element as well as the view.
                    if (selection != null)
                    {
                        try { doc.CurrentSelection.CopyFrom(selection); }
                        catch { /* selection is a convenience, not a requirement */ }
                    }

                    if (!_inDocument)
                    {
                        doc.SavedViewpoints.AddCopy(fresh);
                        _inDocument = true;
                    }
                    else if (MarkupBackend.MarkupTravelsInViewpoint)
                    {
                        // The markup is inside the object, so the document's copy
                        // has to be replaced for it to take the new tag.
                        ReplaceOrAdd(doc, fresh);
                    }

                    transaction.Commit();
                }

                ResolveTracking(doc);

                SavedItem stored = ResolveStored(doc);
                if (stored == null) return false;

                // Releases that keep the markup on the view write it now, with the
                // viewpoint already in the document and made current.
                MarkupBackend.Store(doc, view, stored, _items, _baselineJson);

                // Re-find it: capturing from the current view can hand back a
                // different object, and the name has to be right afterwards.
                ResolveTracking(doc);
                stored = ResolveStored(doc);
                if (stored == null) return false;

                // Only ever rename a viewpoint this add-in created.
                if (_ownViewpoint) Rename(doc, stored);

                // Redlines are only drawn while their viewpoint is the current
                // one. Without this the markup vanishes the instant it is written
                // and only reappears when the viewpoint is clicked in the Saved
                // Viewpoints window. The camera does not move, because the
                // viewpoint was captured from where the user already was. The JSON
                // backend has already done this - it has to write the markup with
                // the viewpoint current.
                if (MarkupBackend.MarkupTravelsInViewpoint) MakeCurrent(doc, stored);

                // Selecting a viewpoint can nudge the camera by a rounding error.
                // Re-reading it here keeps the next tag in this same viewpoint
                // instead of starting another one that looks identical.
                CameraKey after = CameraKey.FromCurrent(doc);
                if (after != null) _cameraKey = after;

                // Did the markup actually land? Read it back rather than assume.
                // An adopted viewpoint has to keep what it already had as well.
                int count = MarkupBackend.Count(doc, view, ResolveStored(doc));
                int expected = _baselineMarkup + _items.Count;

                return count < 0 || count >= expected;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// A new SavedViewpoint for this write: our own camera plus all our
        /// markup, or a fresh copy of the user's viewpoint with our markup added
        /// to what it already held.
        /// </summary>
        private SavedViewpoint BuildViewpointObject()
        {
            if (!_ownViewpoint)
            {
                if (_baseline == null)
                {
                    // Releases that keep markup on the view do not need an object
                    // at all - the viewpoint is already in the document and Store
                    // does the work.
                    return MarkupBackend.MarkupTravelsInViewpoint ? null : PlaceholderForAdopted();
                }

                SavedViewpoint fresh = _baseline.CreateCopy() as SavedViewpoint;
                if (fresh == null) return null;

                if (_guid != Guid.Empty)
                {
                    try { fresh.Guid = _guid; }
                    catch { /* ResolveTracking copes */ }
                }

                // Add to what the user already had; never clear it.
                MarkupBackend.Append(fresh, _items);

                return fresh;
            }

            if (_camera == null) return null;

            SavedViewpoint own = new SavedViewpoint(_camera.CreateCopy());

            if (_guid != Guid.Empty)
            {
                try { own.Guid = _guid; }
                catch { /* the document may re-issue one; ResolveTracking copes */ }
            }

            own.DisplayName = _viewpointName;

            MarkupBackend.Fill(own, _items);

            return own;
        }

        /// <summary>
        /// Something non-null for the write path to carry on with when the
        /// markup does not live in the viewpoint object. It is never added to the
        /// document: an adopted viewpoint is already in it.
        /// </summary>
        private SavedViewpoint PlaceholderForAdopted()
        {
            try
            {
                return _camera == null ? null : new SavedViewpoint(_camera.CreateCopy());
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Replaces the document's copy of the viewpoint in place, so repeated
        /// tags do not pile up duplicate viewpoints. Adds it instead when it has
        /// gone - deleted by the user, or never tracked.
        /// </summary>
        private void ReplaceOrAdd(Document doc, SavedViewpoint fresh)
        {
            SavedItem existing = ResolveStored(doc);

            if (existing == null)
            {
                doc.SavedViewpoints.AddCopy(fresh);
                return;
            }

            GroupItem parent = existing.Parent;

            if (parent != null)
            {
                int index = parent.Children.IndexOf(existing);
                if (index >= 0)
                {
                    doc.SavedViewpoints.ReplaceWithCopy(parent, index, fresh);
                    return;
                }
            }

            int rootIndex = doc.SavedViewpoints.Value.IndexOf(existing);
            if (rootIndex >= 0)
            {
                doc.SavedViewpoints.ReplaceWithCopy(rootIndex, fresh);
                return;
            }

            doc.SavedViewpoints.AddCopy(fresh);
        }

        /// <summary>Names the stored viewpoint, if it is not already right.</summary>
        private void Rename(Document doc, SavedItem stored)
        {
            if (stored == null || string.IsNullOrEmpty(_viewpointName)) return;

            try
            {
                if (string.Equals(stored.DisplayName, _viewpointName, StringComparison.Ordinal)) return;

                using (Transaction transaction = doc.BeginTransaction("Name tag viewpoint"))
                {
                    doc.SavedViewpoints.EditDisplayName(stored, _viewpointName);
                    transaction.Commit();
                }
            }
            catch
            {
                // A wrong name is cosmetic; the markup is what matters.
            }
        }

        /// <summary>
        /// Selects the tag viewpoint in the document so its markup stays visible.
        /// </summary>
        private void MakeCurrent(Document doc, SavedItem item)
        {
            if (item == null) return;

            try
            {
                doc.SavedViewpoints.CurrentSavedViewpoint = item;
            }
            catch
            {
                // Purely a display convenience - the tag is already saved.
            }
        }

        /// <summary>The document's copy of the active tag viewpoint, or null.</summary>
        private SavedItem ResolveStored(Document doc)
        {
            if (_guid == Guid.Empty) return null;

            try
            {
                return doc.SavedViewpoints.ResolveGuid(_guid);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Makes sure we can still find the viewpoint being written to. Copying an
        /// item into the document, or re-capturing it from the current view, can
        /// give it a different guid - so when the guid stops resolving, the item is
        /// re-found by name and adopted.
        /// </summary>
        private void ResolveTracking(Document doc)
        {
            try
            {
                if (!_inDocument) return;
                if (ResolveStored(doc) != null) return;

                SavedItem match = FindByName(doc, _viewpointName);
                if (match == null) match = FindByName(doc, _previousViewpointName);

                if (match != null)
                {
                    _guid = match.Guid;
                    return;
                }

                // It cannot be tracked any more, so the next write starts a new
                // viewpoint rather than silently writing over someone else's.
                _inDocument = false;
                _guid = Guid.Empty;
            }
            catch
            {
                _inDocument = false;
            }
        }

        /// <summary>The last viewpoint with this display name, anywhere in the tree.</summary>
        private static SavedItem FindByName(Document doc, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            SavedItem found = null;

            try
            {
                foreach (SavedItem item in EnumerateAll(doc.SavedViewpoints.RootItem))
                {
                    if (item == null || item.IsGroup) continue;
                    if (string.Equals(item.DisplayName, name, StringComparison.Ordinal)) found = item;
                }
            }
            catch
            {
                return null;
            }

            return found;
        }
    }
}
