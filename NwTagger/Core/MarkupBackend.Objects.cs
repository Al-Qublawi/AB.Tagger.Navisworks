using System.Collections.Generic;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Interop;

namespace NwTagger.Core
{
    /// <summary>
    /// Markup storage for Navisworks 2025 and 2026, which expose the redline
    /// list on a viewpoint directly:
    ///
    ///     SavedViewpoint.EditRedlines().Add(new LcOpRedlineText(...))
    ///
    /// Markup is written straight into the in-memory viewpoint before it is
    /// pushed to the document, so nothing extra is needed afterwards.
    ///
    /// 2027 removed these types; <c>MarkupBackend.Json.cs</c> replaces this file
    /// in that build, selected by the NW_JSON_REDLINES constant in the csproj.
    /// Both files expose the same two methods.
    /// </summary>
    internal static class MarkupBackend
    {
        /// <summary>Which storage route this build was compiled for.</summary>
        internal const string Description = "viewpoint redline list (2025 / 2026)";

        /// <summary>
        /// Replaces the viewpoint's markup with <paramref name="items"/>.
        /// Called before the viewpoint is written to the document.
        /// </summary>
        internal static void Prepare(Document doc, View view, SavedViewpoint master, IList<MarkupItem> items)
        {
            if (master == null) return;

            LcOpRedlineList list = master.EditRedlines();
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
        /// Nothing to do: the markup travelled inside the viewpoint that was just
        /// added to the document.
        /// </summary>
        internal static void Commit(Document doc, View view, SavedItem stored)
        {
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
