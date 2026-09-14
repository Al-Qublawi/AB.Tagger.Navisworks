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
    }

    /// <summary>
    /// Owns the saved viewpoint the tags are being written into.
    ///
    /// The master SavedViewpoint is held in memory and is the single source of
    /// truth for the redline list. Items already inside the document are
    /// read-only, so rather than round-tripping through the document we
    /// accumulate here and push the whole object down each time.
    /// </summary>
    public sealed class ViewpointService
    {
        private static readonly ViewpointService _current = new ViewpointService();
        public static ViewpointService Current { get { return _current; } }

        private ViewpointService() { }

        /// <summary>Matches names this add-in generates, e.g. "Tag 07" or "Tag 07-09".</summary>
        private static readonly Regex TagNamePattern =
            new Regex(@"^Tag\s+(\d+)(?:\s*-\s*(\d+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private SavedViewpoint _master;
        private Guid _guid;
        private bool _inDocument;
        private CameraKey _camera;
        private string _viewpointName;

        /// <summary>
        /// Every markup item in the active viewpoint. Held here rather than read
        /// back from the document, so the backends can rebuild the whole set
        /// without either of them needing to enumerate existing markup.
        /// </summary>
        private readonly List<MarkupItem> _items = new List<MarkupItem>();

        private int _nextTagNumber = 1;
        private bool _numberingInitialised;

        private int _firstTagInViewpoint;
        private int _lastTagInViewpoint;

        /// <summary>Forgets the active viewpoint, so the next tag starts a new one.</summary>
        public void Reset()
        {
            _master = null;
            _guid = Guid.Empty;
            _inDocument = false;
            _camera = null;
            _viewpointName = null;
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
            bool sameCamera = _inDocument && _master != null && _camera != null && _camera.Matches(now);

            if (!sameCamera)
            {
                StartNewViewpoint(doc, now, tagNumber);
            }
            else
            {
                // Widen the viewpoint name to cover every tag it now holds.
                _lastTagInViewpoint = tagNumber;
                _viewpointName = _firstTagInViewpoint == _lastTagInViewpoint
                    ? FormatTagName(_firstTagInViewpoint)
                    : FormatTagName(_firstTagInViewpoint) + "-" + tagNumber.ToString("00", CultureInfo.InvariantCulture);
            }

            if (markup != null) _items.AddRange(markup);

            // Hand the complete set to whichever storage route this build uses:
            // the viewpoint's redline list (2025/2026) or the view's JSON (2027).
            MarkupBackend.Prepare(doc, view, _master, _items);

            _master.DisplayName = _viewpointName;

            PushToDocument(doc, view, selection);

            return new ViewpointResult
            {
                TagName = tagName,
                ViewpointName = _viewpointName,
                Reused = sameCamera,
                ViewpointGuid = _guid
            };
        }

        private static string FormatTagName(int number)
        {
            return "Tag " + number.ToString("00", CultureInfo.InvariantCulture);
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
            Viewpoint copy = doc.CurrentViewpoint.CreateCopy();

            _master = new SavedViewpoint(copy);
            _guid = Guid.NewGuid();
            _camera = camera;
            _inDocument = false;

            // A new viewpoint starts with no markup of its own.
            _items.Clear();

            _firstTagInViewpoint = tagNumber;
            _lastTagInViewpoint = tagNumber;
            _viewpointName = FormatTagName(tagNumber);

            try
            {
                _master.Guid = _guid;
            }
            catch
            {
                _guid = Guid.Empty;
            }

            _master.DisplayName = _viewpointName;
        }

        /// <summary>
        /// Writes the master viewpoint into the document, adding it the first time
        /// and replacing it in place afterwards so repeat tags do not pile up
        /// duplicate viewpoints.
        /// </summary>
        private void PushToDocument(Document doc, View view, ModelItemCollection selection)
        {
            using (Transaction transaction = doc.BeginTransaction(_inDocument ? "Update tag viewpoint" : "Add tag viewpoint"))
            {
                // Storing the selection alongside the camera makes the viewpoint
                // restore the tagged element as well as the view.
                if (selection != null)
                {
                    try { doc.CurrentSelection.CopyFrom(selection); }
                    catch { /* selection is a convenience, not a requirement */ }
                }

                if (!_inDocument)
                {
                    doc.SavedViewpoints.AddCopy(_master);
                    _inDocument = true;
                    ResolveGuidAfterAdd(doc);
                }
                else
                {
                    ReplaceExisting(doc);
                }

                // 2027 needs the stored viewpoint to capture the markup that is
                // on the live view; the object backend does nothing here.
                SavedItem stored = ResolveStored(doc);
                if (stored != null) MarkupBackend.Commit(doc, view, stored);

                transaction.Commit();
            }

            // Redlines are only drawn while their viewpoint is the current one.
            // Without this the markup vanishes the instant it is written and only
            // reappears when the viewpoint is clicked in the Saved Viewpoints
            // window. Making it current keeps the tag on screen, and because the
            // viewpoint was captured from the live camera the view does not move.
            // Navigating away then behaves like any other Navisworks viewpoint.
            MakeCurrent(doc);
        }

        /// <summary>
        /// Selects the tag viewpoint in the document so its markup stays visible.
        /// </summary>
        private void MakeCurrent(Document doc)
        {
            SavedItem item = ResolveStored(doc);
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
        /// AddCopy stores a copy, which may not preserve our guid. Re-find the
        /// document item so later replacements target the right entry.
        /// </summary>
        private void ResolveGuidAfterAdd(Document doc)
        {
            try
            {
                if (_guid != Guid.Empty && doc.SavedViewpoints.ResolveGuid(_guid) != null)
                    return;

                SavedItemCollection roots = doc.SavedViewpoints.Value;
                int index = roots.IndexOfDisplayName(_viewpointName);

                if (index >= 0)
                {
                    SavedItem[] items = new SavedItem[roots.Count];
                    roots.CopyTo(items, 0);
                    _guid = items[index].Guid;
                }
                else
                {
                    // Cannot track it - fall back to always creating a new viewpoint.
                    _inDocument = false;
                }
            }
            catch
            {
                _inDocument = false;
            }
        }

        private void ReplaceExisting(Document doc)
        {
            SavedItem existing = null;

            try
            {
                if (_guid != Guid.Empty)
                    existing = doc.SavedViewpoints.ResolveGuid(_guid);
            }
            catch
            {
                existing = null;
            }

            if (existing == null)
            {
                // Someone deleted or moved it - start over rather than throwing.
                doc.SavedViewpoints.AddCopy(_master);
                ResolveGuidAfterAdd(doc);
                return;
            }

            // If AddCopy gave the document item a different guid than the master
            // carries, align them before replacing - otherwise the replacement
            // writes the master's old guid back and we lose track of the item.
            try
            {
                _master.Guid = _guid;
            }
            catch
            {
                // Non-fatal: the lookups below still work by index.
            }

            GroupItem parent = existing.Parent;

            if (parent != null)
            {
                int index = parent.Children.IndexOf(existing);
                if (index >= 0)
                {
                    doc.SavedViewpoints.ReplaceWithCopy(parent, index, _master);
                    return;
                }
            }

            int rootIndex = doc.SavedViewpoints.Value.IndexOf(existing);
            if (rootIndex >= 0)
            {
                doc.SavedViewpoints.ReplaceWithCopy(rootIndex, _master);
                return;
            }

            doc.SavedViewpoints.AddCopy(_master);
            ResolveGuidAfterAdd(doc);
        }
    }
}
