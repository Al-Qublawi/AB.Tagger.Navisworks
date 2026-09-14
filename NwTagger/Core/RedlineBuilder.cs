using System;
using System.Collections.Generic;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Interop;

namespace NwTagger.Core
{
    /// <summary>
    /// Turns screen-space geometry into camera-space markup.
    ///
    /// Markup lives in "camera space", not pixels. LcOpRedline exposes the
    /// conversion as a static helper, and it is public in every supported
    /// release - 2025, 2026 and 2027 - so this stays version-neutral:
    ///
    ///     Point2D camera = LcOpRedline.ScreenToCameraSpace(view.Viewer, x, y);
    ///
    /// Because camera-space units are not documented, every pixel offset (line
    /// spacing in particular) is converted by transforming two screen points and
    /// taking the delta, rather than assuming a scale factor.
    ///
    /// How the result is stored differs per release, and that is
    /// <see cref="MarkupBackend"/>'s problem, not this class's.
    /// </summary>
    public static class RedlineBuilder
    {
        /// <summary>
        /// Builds the leader arrow plus one text entry per line.
        /// Returns an empty list if the conversion is unavailable.
        /// </summary>
        /// <param name="view">The view the click happened in.</param>
        /// <param name="lines">Already-wrapped text lines.</param>
        /// <param name="anchorX">Element point, in pixels - where the arrow head goes.</param>
        /// <param name="anchorY">Element point, in pixels.</param>
        /// <param name="textX">Top-left of the text block, in pixels.</param>
        /// <param name="textY">Top-left of the text block, in pixels.</param>
        /// <param name="attachX">Where the leader leaves the text block, in pixels.</param>
        /// <param name="attachY">Where the leader leaves the text block, in pixels.</param>
        /// <param name="settings">Colour and thickness to apply.</param>
        public static List<MarkupItem> Build(
            View view,
            IList<string> lines,
            int anchorX, int anchorY,
            int textX, int textY,
            int attachX, int attachY,
            TaggerSettings settings)
        {
            List<MarkupItem> markup = new List<MarkupItem>();
            if (view == null || lines == null || lines.Count == 0) return markup;

            LcOaViewer viewer = view.Viewer;
            if (viewer == null) return markup;

            int lineHeight = TextLayout.LineHeight(settings.TextSize);

            Point2D anchor = LcOpRedline.ScreenToCameraSpace(viewer, anchorX, anchorY);
            Point2D origin = LcOpRedline.ScreenToCameraSpace(viewer, textX, textY);
            Point2D attach = LcOpRedline.ScreenToCameraSpace(viewer, attachX, attachY);

            // Derive the camera-space line step from a known pixel step, so the
            // spacing is correct whatever the camera-space scale turns out to be.
            Point2D nextLine = LcOpRedline.ScreenToCameraSpace(viewer, textX, textY + lineHeight);
            double stepX = nextLine.X - origin.X;
            double stepY = nextLine.Y - origin.Y;

            System.Drawing.Color colour = settings.TextColor;
            double r = colour.R / 255.0;
            double g = colour.G / 255.0;
            double b = colour.B / 255.0;

            // Leader first, so the text paints over it rather than under it.
            // It starts at the edge of the text block facing the element, so it
            // never runs back across its own text, and the head lands on the
            // element, matching the reference markups.
            markup.Add(new MarkupArrow
            {
                StartX = attach.X,
                StartY = attach.Y,
                EndX = anchor.X,
                EndY = anchor.Y,
                Thickness = settings.LeaderWidth,
                ColorR = r,
                ColorG = g,
                ColorB = b
            });

            for (int i = 0; i < lines.Count; i++)
            {
                string text = lines[i];
                if (string.IsNullOrEmpty(text)) continue;

                markup.Add(new MarkupText
                {
                    OriginX = origin.X + stepX * i,
                    OriginY = origin.Y + stepY * i,
                    Text = text,
                    ColorR = r,
                    ColorG = g,
                    ColorB = b
                });
            }

            return markup;
        }

        /// <summary>Converts a WinForms colour to the Navisworks 0..1 RGB colour.</summary>
        public static Color ToNavisworksColour(System.Drawing.Color c)
        {
            return Color.FromByteRGB(c.R, c.G, c.B);
        }

        /// <summary>Converts a Navisworks 0..1 RGB colour back to a WinForms colour.</summary>
        public static System.Drawing.Color ToDrawingColour(Color c)
        {
            int r = ClampByte(c.R * 255.0);
            int g = ClampByte(c.G * 255.0);
            int b = ClampByte(c.B * 255.0);
            return System.Drawing.Color.FromArgb(r, g, b);
        }

        private static int ClampByte(double v)
        {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return (int)Math.Round(v);
        }
    }
}
