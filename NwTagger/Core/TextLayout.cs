using System;
using System.Collections.Generic;

namespace NwTagger.Core
{
    /// <summary>
    /// Text wrapping and tag placement geometry.
    /// Everything here works in screen pixels; conversion to redline camera
    /// space happens in <see cref="RedlineBuilder"/>.
    /// </summary>
    public static class TextLayout
    {
        /// <summary>Minimum leader length in pixels, so the arrow is always readable.</summary>
        public const int MinimumLeaderLength = 45;

        /// <summary>Gap between the placement click and the first character.</summary>
        public const int TextPadding = 6;

        /// <summary>
        /// Wraps each source line to <paramref name="maxChars"/>, breaking on
        /// whitespace where possible. A "Category: value" line that must wrap gets
        /// its continuation indented so the tag stays readable.
        /// </summary>
        public static List<string> Wrap(IEnumerable<string> lines, bool enabled, int maxChars)
        {
            List<string> result = new List<string>();
            if (lines == null) return result;

            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;

                if (!enabled || line.Length <= maxChars)
                {
                    result.Add(line);
                    continue;
                }

                result.AddRange(WrapSingle(line, maxChars));
            }

            return result;
        }

        private static IEnumerable<string> WrapSingle(string line, int maxChars)
        {
            List<string> parts = new List<string>();
            if (maxChars < 4) maxChars = 4;

            string remaining = line;
            bool first = true;

            while (remaining.Length > maxChars)
            {
                int breakAt = remaining.LastIndexOf(' ', Math.Min(maxChars, remaining.Length - 1));

                // No sensible whitespace break - split hard rather than overflow.
                if (breakAt <= 0) breakAt = maxChars;

                string chunk = remaining.Substring(0, breakAt).TrimEnd();
                parts.Add(first ? chunk : "  " + chunk);

                remaining = remaining.Substring(breakAt).TrimStart();
                first = false;
            }

            if (remaining.Length > 0)
                parts.Add(first ? remaining : "  " + remaining);

            return parts;
        }

        /// <summary>
        /// Estimates the pixel width of the widest line, used for edge collision.
        /// Redline text is rendered by Navisworks, so this is an approximation:
        /// average glyph advance is about 0.55 em for the UI fonts in play.
        /// </summary>
        public static int EstimateWidth(IList<string> lines, int pointSize)
        {
            int longest = 0;
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Length > longest) longest = lines[i].Length;

            return (int)Math.Round(longest * pointSize * 0.55);
        }

        /// <summary>Line-to-line spacing in pixels for a given point size.</summary>
        public static int LineHeight(int pointSize)
        {
            int h = (int)Math.Round(pointSize * 1.45);
            return h < 8 ? 8 : h;
        }

        /// <summary>
        /// Works out where the leader should leave the text block.
        ///
        /// The leader attaches to whichever edge of the block faces the element,
        /// so it never cuts back across its own text. With the text up and to the
        /// left of the element the leader leaves the bottom-right corner; with the
        /// text to the right of the element it leaves the left edge, and so on.
        /// </summary>
        /// <param name="textX">Left of the text block, in pixels.</param>
        /// <param name="textY">Top of the first line, in pixels.</param>
        public static void LeaderAttachPoint(
            int textX, int textY,
            int blockWidth, int lineCount, int lineHeight,
            int anchorX, int anchorY,
            out int attachX, out int attachY)
        {
            int left = textX;
            int right = textX + Math.Max(1, blockWidth);
            int top = textY;
            int bottom = textY + Math.Max(1, lineCount) * lineHeight;

            // Horizontal: leave from the side the element sits on. When the element
            // is somewhere above or below the block, leave from the middle.
            if (anchorX > right) attachX = right;
            else if (anchorX < left) attachX = left;
            else attachX = (left + right) / 2;

            // Vertical: same rule, so the leader starts on the corner or edge
            // closest to the element rather than always on the first line.
            if (anchorY > bottom) attachY = bottom;
            else if (anchorY < top) attachY = top;
            else attachY = (top + bottom) / 2;
        }

        /// <summary>
        /// Adjusts the requested text position so the tag stays inside the view and
        /// keeps a readable distance from the element it points at.
        ///
        /// - pushes the text away from the anchor if the click was too close
        /// - flips the block left when it would overflow the right edge
        /// - clamps vertically so no line falls off the top or bottom
        /// </summary>
        public static void ApplySmartOffset(
            int anchorX, int anchorY,
            ref int textX, ref int textY,
            int blockWidth, int lineCount, int lineHeight,
            int viewWidth, int viewHeight)
        {
            // 1. Enforce a minimum leader length.
            double dx = textX - anchorX;
            double dy = textY - anchorY;
            double distance = Math.Sqrt(dx * dx + dy * dy);

            if (distance < MinimumLeaderLength)
            {
                if (distance < 0.5)
                {
                    // Degenerate click - default to up and to the right.
                    dx = 0.7071;
                    dy = -0.7071;
                }
                else
                {
                    dx /= distance;
                    dy /= distance;
                }

                textX = anchorX + (int)Math.Round(dx * MinimumLeaderLength);
                textY = anchorY + (int)Math.Round(dy * MinimumLeaderLength);
            }

            // 2. Horizontal: flip the block to the left of the click if it would overflow.
            int blockHeight = Math.Max(1, lineCount) * lineHeight;

            if (textX + TextPadding + blockWidth > viewWidth)
            {
                int flipped = textX - TextPadding - blockWidth;
                textX = flipped >= 0 ? flipped : Math.Max(0, viewWidth - blockWidth - TextPadding);
            }
            else
            {
                textX += TextPadding;
            }

            // 3. Vertical: keep every line on screen.
            if (textY < lineHeight) textY = lineHeight;
            if (textY + blockHeight > viewHeight) textY = Math.Max(lineHeight, viewHeight - blockHeight);

            if (textX < 0) textX = 0;
        }
    }
}
