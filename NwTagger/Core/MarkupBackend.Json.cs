using System.Collections.Generic;
using Autodesk.Navisworks.Api;

namespace NwTagger.Core
{
    /// <summary>
    /// Markup storage for Navisworks 2027, which made LcOpRedlineList internal
    /// and removed SavedViewpoint.EditRedlines(). The remaining public route is
    /// a JSON string on the live view:
    ///
    ///     SavedViewpoints.CurrentSavedViewpoint = stored
    ///     View.SetRedlines(json)
    ///     SavedViewpoints.ReplaceFromCurrentView(stored)
    ///
    /// Order matters, and getting it wrong is what made tags vanish. The markup
    /// is on the *view*, and selecting a saved viewpoint re-applies that
    /// viewpoint's own markup to the view. So the viewpoint is made current
    /// first, the markup is written after that, and only then is it captured.
    /// Writing the markup first - as this did until 1.2.0 - left a window in
    /// which Navisworks re-applied the viewpoint, wiped the view, and the
    /// capture then stored an empty redline set over the real one.
    ///
    /// The format is in <see cref="RedlineJson"/>, captured from a live 2027
    /// session and verified to round-trip byte for byte.
    ///
    /// <c>MarkupBackend.Objects.cs</c> replaces this file for 2024 - 2026,
    /// selected by -p:RedlineBackend=Json. Both files expose the same four
    /// members, and <see cref="ViewpointService"/> uses nothing else.
    /// </summary>
    internal static class MarkupBackend
    {
        /// <summary>Which storage route this build was compiled for.</summary>
        internal const string Description = "view redline JSON (2027)";

        /// <summary>
        /// False: the markup lives on the view, not in the viewpoint object, so a
        /// viewpoint already in the document is never replaced to update its
        /// markup - it is made current and re-captured instead.
        /// </summary>
        internal static readonly bool MarkupTravelsInViewpoint = false;

        /// <summary>
        /// Nothing to do: a viewpoint object carries no markup in this release.
        /// </summary>
        internal static void Fill(SavedViewpoint fresh, IList<MarkupItem> items)
        {
        }

        /// <summary>
        /// Writes the markup onto the live view and captures it into the stored
        /// viewpoint, which must already be in the document.
        /// </summary>
        internal static void Store(Document doc, View view, SavedItem stored, IList<MarkupItem> items)
        {
            SavedViewpoint viewpoint = stored as SavedViewpoint;
            if (doc == null || view == null || viewpoint == null) return;

            // Make it current first. The camera does not move - the viewpoint was
            // captured from this very view - but it does bind the view's redlines
            // to this viewpoint, so what is written next belongs to it.
            try { doc.SavedViewpoints.CurrentSavedViewpoint = stored; }
            catch { /* the write below is still worth attempting */ }

            // The caller holds every item for this viewpoint, so serialise the
            // whole set - this replaces whatever is on the view.
            view.TrySetRedlines(RedlineJson.Serialize(items));

            // Pull the view's markup into the stored viewpoint. Without this the
            // viewpoint keeps only the camera and the markup is lost as soon as
            // the view changes.
            try
            {
                using (Transaction transaction = doc.BeginTransaction("Capture tag markup"))
                {
                    doc.SavedViewpoints.ReplaceFromCurrentView(viewpoint);
                    transaction.Commit();
                }
            }
            catch
            {
                // The tag is still on screen; only persistence failed. The caller
                // checks Count below and writes it again.
            }
        }

        /// <summary>
        /// How much markup the viewpoint now holds, or -1 when it cannot be read.
        ///
        /// In this release the viewpoint's markup is only readable through the
        /// view it was just captured from, so this reads the view. It still
        /// catches the failure that mattered: markup wiped between being written
        /// and being captured comes back as 0.
        /// </summary>
        internal static int Count(Document doc, View view, SavedItem stored)
        {
            if (view == null) return -1;

            try
            {
                string json = view.GetRedlines();
                if (string.IsNullOrEmpty(json)) return 0;

                List<MarkupItem> items = RedlineJson.Parse(json);
                return items == null ? -1 : items.Count;
            }
            catch
            {
                return -1;
            }
        }
    }
}
