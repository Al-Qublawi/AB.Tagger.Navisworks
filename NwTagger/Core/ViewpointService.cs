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

        internal bool Matches(CameraKey other)
        {
            if (other == null) return false;

            return Near(_x, other._x, PositionTolerance)
                && Near(_y, other._y, PositionTolerance)
                && Near(_z, other._z, PositionTolerance)
                && Near(_a, other._a, RotationTolerance)
                && Near(_b, other._b, RotationTolerance)
                && Near(_c, other._c, RotationTolerance)
                && Near(_d, other._d, RotationTolerance)
                && Near(_heightField, other._heightField, FieldTolerance)
                && _projection == other._projection;
        }

        private static bool Near(double a, double b, double tolerance)
        {
            return Math.Abs(a - b) <= tolerance;
        }
    }

    /// <summary>Outcome of writing a tag into a saved viewpoint.</summary>
    public sealed class ViewpointResult
    {
        /// <summary>This tag's own sequential name, e.g. "Tag 03".</summary>
        public string TagName { get; set; }

        /// <summary>
        /// Name of the viewpoint holding it. Equals <see cref="TagName"/> for a
        /// single-tag viewpoint, or a range like "Tag 01-03" when several tags
        /// were placed without moving the camera.
        /// </summary>
        public string ViewpointName { get; set; }

        public bool Reused { get; set; }

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
    /// What it keeps between tags is the *camera* and the list of markup, never a
    /// viewpoint object. Every write builds a brand new SavedViewpoint from that
    /// camera and pushes it down; nothing the document has already copied is
    /// written to a second time.
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

        /// <summary>The camera the active viewpoint was captured from.</summary>
        private Viewpoint _camera;

        private CameraKey _cameraKey;
        private Guid _guid;
        private bool _inDocument;
        private string _viewpointName;

        /// <summary>The name before the last widening, used to re-find the item.</summary>
        private string _previousViewpointName;

        /// <summary>
        /// Every markup item in the active viewpoint. Held here rather than read
        /// back from the document, so each write can rebuild the whole set.
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
        /// Adds redlines to the saved viewpoint for the current camera, creating
        /// one if the user has moved (or if this is the first tag).
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
            bool sameCamera = _inDocument && _camera != null && _cameraKey != null && _cameraKey.Matches(now);

            // This tag's own markup is kept aside: if the shared viewpoint turns
            // out not to take it, the tag still gets a viewpoint of its own.
            List<MarkupItem> mine = new List<MarkupItem>();
            if (markup != null) mine.AddRange(markup);

            if (!sameCamera) StartNewViewpoint(doc, now, tagNumber);
            else WidenName(tagNumber);

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
                    sameCamera = false;

                    warning = TryWrite(doc, view, selection)
                        ? "The earlier viewpoint would not take more markup, so " + tagName
                            + " was saved in a viewpoint of its own."
                        : "Navisworks would not store the markup for " + tagName + ".";
                }
            }

            return new ViewpointResult
            {
                TagName = tagName,
                ViewpointName = _viewpointName,
                Reused = sameCamera,
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
                MarkupBackend.Store(doc, view, stored, _items);

                // Re-find it: capturing from the current view can hand back a
                // different object, and the name has to be right afterwards.
                ResolveTracking(doc);
                stored = ResolveStored(doc);
                if (stored == null) return false;

                Rename(doc, stored);

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
                int count = MarkupBackend.Count(doc, view, ResolveStored(doc));

                return count < 0 || count >= _items.Count;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// A new SavedViewpoint for the active camera, carrying the markup when
        /// this release stores it that way.
        /// </summary>
        private SavedViewpoint BuildViewpointObject()
        {
            if (_camera == null) return null;

            SavedViewpoint fresh = new SavedViewpoint(_camera.CreateCopy());

            if (_guid != Guid.Empty)
            {
                try { fresh.Guid = _guid; }
                catch { /* the document may re-issue one; ResolveTracking copes */ }
            }

            fresh.DisplayName = _viewpointName;

            MarkupBackend.Fill(fresh, _items);

            return fresh;
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
