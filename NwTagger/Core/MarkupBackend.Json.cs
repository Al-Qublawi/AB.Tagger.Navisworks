using System.Collections.Generic;
using Autodesk.Navisworks.Api;

namespace NwTagger.Core
{
    /// <summary>
    /// Markup storage for Navisworks 2027, which made LcOpRedlineList internal
    /// and removed SavedViewpoint.EditRedlines(). The remaining public route is
    /// a JSON string on the live view:
    ///
    ///     View.SetRedlines(json)
    ///     SavedViewpoints.ReplaceFromCurrentView(savedViewpoint)
    ///
    /// So markup goes onto the view first, and the viewpoint then captures what
    /// is on screen. The format is in <see cref="RedlineJson"/>, captured from a
    /// live 2027 session and verified to round-trip byte for byte.
    ///
    /// This whole sequence was proven end to end in 2027 before being adopted:
    /// generate, apply, save, and read back from the stored viewpoint.
    ///
    /// <c>MarkupBackend.Objects.cs</c> replaces this file for 2025 and 2026,
    /// selected by the NW_JSON_REDLINES constant in the csproj.
    /// </summary>
    internal static class MarkupBackend
    {
        /// <summary>Which storage route this build was compiled for.</summary>
        internal const string Description = "view redline JSON (2027)";

        /// <summary>
        /// Puts the markup on the live view. The viewpoint picks it up in
        /// <see cref="Commit"/>, once it exists in the document.
        /// </summary>
        internal static void Prepare(Document doc, View view, SavedViewpoint master, IList<MarkupItem> items)
        {
            if (view == null) return;

            // The caller holds every item for this viewpoint, so serialise the
            // whole set - this replaces whatever is on the view.
            string json = RedlineJson.Serialize(items);

            view.TrySetRedlines(json);
        }

        /// <summary>
        /// Pulls the live view's markup into the stored viewpoint. Without this
        /// the viewpoint keeps only the camera and the markup is lost as soon as
        /// the view changes.
        /// </summary>
        internal static void Commit(Document doc, View view, SavedItem stored)
        {
            SavedViewpoint viewpoint = stored as SavedViewpoint;
            if (doc == null || viewpoint == null) return;

            try
            {
                doc.SavedViewpoints.ReplaceFromCurrentView(viewpoint);
            }
            catch
            {
                // The tag is still on screen; only persistence failed.
            }
        }
    }
}
