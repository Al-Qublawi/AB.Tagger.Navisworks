using System.Collections.Generic;

namespace NwTagger.Core
{
    /// <summary>
    /// One piece of markup, in camera space, independent of how it gets stored.
    ///
    /// 2026 turns these into LcOpRedline objects and adds them to a viewpoint's
    /// redline list. 2027 removed that API, so it serialises them to JSON and
    /// hands the string to View.SetRedlines instead. Everything upstream - the
    /// picking, the text layout, the leader geometry - is shared, and works in
    /// this one representation.
    /// </summary>
    public abstract class MarkupItem
    {
        /// <summary>Red component, 0..1.</summary>
        public double ColorR { get; set; }

        /// <summary>Green component, 0..1.</summary>
        public double ColorG { get; set; }

        /// <summary>Blue component, 0..1.</summary>
        public double ColorB { get; set; }
    }

    /// <summary>A line of text at a camera-space origin.</summary>
    public sealed class MarkupText : MarkupItem
    {
        public double OriginX { get; set; }
        public double OriginY { get; set; }

        public string Text { get; set; }
    }

    /// <summary>A leader line with an arrow head at <see cref="EndX"/>/<see cref="EndY"/>.</summary>
    public sealed class MarkupArrow : MarkupItem
    {
        public double StartX { get; set; }
        public double StartY { get; set; }

        public double EndX { get; set; }
        public double EndY { get; set; }

        public int Thickness { get; set; }
    }

    /// <summary>The markup produced by a single tag.</summary>
    public sealed class TagMarkup
    {
        public TagMarkup()
        {
            Items = new List<MarkupItem>();
        }

        /// <summary>Leader first, then one entry per text line.</summary>
        public List<MarkupItem> Items { get; private set; }
    }
}
