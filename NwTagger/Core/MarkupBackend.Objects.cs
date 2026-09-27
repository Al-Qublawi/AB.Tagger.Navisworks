using System.Collections.Generic;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Interop;

namespace NwTagger.Core
{
    /// <summary>
    /// Markup storage for Navisworks 2024, 2025 and 2026, which expose the
    /// redline list on a viewpoint directly:
    ///
    ///     SavedViewpoint.EditRedlines().Add(new LcOpRedlineText(...))
    ///
    /// The markup rides inside the viewpoint object, so it reaches the document
    /// with the copy that <see cref="ViewpointService"/> pushes down.
    ///
    /// 2027 removed these types; <c>MarkupBackend.Json.cs</c> replaces this file
    /// in that build, selected by -p:RedlineBackend=Json. Both files expose the
    /// same four members, and <see cref="ViewpointService"/> uses nothing else.
    /// </summary>
    internal static class MarkupBackend
    {
        /// <summary>Which storage route this build was compiled for.</summary>
        internal const string Description = "viewpoint redline list (2024 - 2026)";

        /// <summary>
        /// True: the markup is part of the viewpoint object, so updating a
        /// viewpoint that is already in the document means replacing it.
        /// </summary>
        internal static readonly bool MarkupTravelsInViewpoint = true;

        /// <summary>
        /// Puts <paramref name="items"/> into a viewpoint object that has not been
        /// added to the document yet.
        ///
        /// The object is always a freshly built one - never one that has already
        /// been copied into the document. A reused object hands back a redline
        /// list whose native handle no longer belongs to it, and writes to it are
        /// silently lost, which is how a viewpoint ended up in the document with
        /// none of its tags in it.
        /// </summary>
        internal static void Fill(SavedViewpoint fresh, IList<MarkupItem> items)
        {
            if (fresh == null) return;

            LcOpRedlineList list = fresh.EditRedlines();
            if (list == null) return;

            // The caller holds every item for this viewpoint, so rebuild the
            // whole list rather than tracking what was already added.
            list.Clear();

            if (items == null) return;

            foreach (MarkupItem item in items)
            {
                LcOpRedline redline = Convert(item);
                if (redline != null) list.Add(redline);
            }
        }

        /// <summary>
        /// Nothing to do: the markup travelled inside the viewpoint that
        /// <see cref="ViewpointService"/> just wrote to the document.
        /// </summary>
        internal static void Store(Document doc, View view, SavedItem stored, IList<MarkupItem> items)
        {
        }

        /// <summary>
        /// How many markup objects the document's copy of the viewpoint really
        /// holds, or -1 when it cannot be read. This is read straight out of the
        /// stored item, so it is the truth rather than what we hoped we wrote.
        /// </summary>
        internal static int Count(Document doc, View view, SavedItem stored)
        {
            SavedViewpoint viewpoint = stored as SavedViewpoint;
            if (viewpoint == null) return -1;

            try
            {
                LcOpRedlineList list = viewpoint.Redlines;
                // Size() comes from LcOpRedlineListBase - LcOpRedlineList itself
                // only publishes Add and Clear.
                return list == null ? -1 : list.Size();
            }
            catch
            {
                return -1;
            }
        }

        private static LcOpRedline Convert(MarkupItem item)
        {
            Color colour = Color.FromByteRGB(
                ToByte(item.ColorR), ToByte(item.ColorG), ToByte(item.ColorB));

            MarkupArrow arrow = item as MarkupArrow;

            if (arrow != null)
            {
                LcOpRedlineArrow redline = new LcOpRedlineArrow(
                    new Point2D(arrow.StartX, arrow.StartY),
                    new Point2D(arrow.EndX, arrow.EndY));

                Style(redline, colour, arrow.Thickness);
                return redline;
            }

            MarkupText text = item as MarkupText;

            if (text != null)
            {
                LcOpRedlineText redline = new LcOpRedlineText(
                    text.Text ?? string.Empty,
                    new Point2D(text.OriginX, text.OriginY));

                Style(redline, colour, 1);
                return redline;
            }

            return null;
        }

        private static void Style(LcOpRedline redline, Color colour, int thickness)
        {
            try
            {
                redline.SetLineColor(colour);
                redline.SetLineThickness(thickness);
            }
            catch
            {
                // Styling is cosmetic - never let it stop the tag being created.
            }
        }

        private static byte ToByte(double value)
        {
            int v = (int)System.Math.Round(value * 255.0);
            if (v < 0) v = 0;
            if (v > 255) v = 255;
            return (byte)v;
        }
    }
}
