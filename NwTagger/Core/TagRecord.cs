using System;
using System.Collections.Generic;

namespace NwTagger.Core
{
    /// <summary>
    /// One placed tag.
    ///
    /// As well as the reporting fields, this keeps the geometry needed to redraw
    /// the markup onto an exported photo. Positions are stored normalised
    /// (0..1 of the view width and height) so they survive being rendered at a
    /// different resolution than the one they were placed at.
    /// </summary>
    public sealed class TagRecord
    {
        public DateTime CreatedUtc { get; set; }

        /// <summary>Sequential name, e.g. "Tag 03".</summary>
        public string TagName { get; set; }

        /// <summary>Source file the tagged element came from.</summary>
        public string FileName { get; set; }

        /// <summary>Viewpoint this tag lives in, for photographing it later.</summary>
        public Guid ViewpointGuid { get; set; }

        public string ViewpointName { get; set; }
        public bool ReusedViewpoint { get; set; }

        /// <summary>
        /// Set when the markup had to be repaired or moved to a viewpoint of its
        /// own on the way in. Shown on the panel's status line; never exported.
        /// </summary>
        public string Warning { get; set; }

        public string ElementId { get; set; }
        public string DisplayName { get; set; }

        /// <summary>The wrapped text lines exactly as written to the markup.</summary>
        public List<string> Lines { get; set; }

        // ---- Markup geometry, normalised against the view it was placed in ----
        public double AnchorU { get; set; }
        public double AnchorV { get; set; }
        public double TextU { get; set; }
        public double TextV { get; set; }

        /// <summary>Where the leader leaves the text block, facing the element.</summary>
        public double AttachU { get; set; }
        public double AttachV { get; set; }

        // ---- Style at the time of placement ----
        public int ColorArgb { get; set; }
        public int TextSize { get; set; }
        public int LeaderWidth { get; set; }

        /// <summary>Aspect ratio of the view when placed, used to size the photo.</summary>
        public double ViewAspect { get; set; }

        /// <summary>
        /// Pixel height of the view when placed. The exported photo is rendered at
        /// a different size, so text and spacing are scaled by the ratio of the
        /// two heights to keep the markup looking the same.
        /// </summary>
        public int SourceViewHeight { get; set; }

        public string Text
        {
            get
            {
                return Lines == null ? string.Empty : string.Join(Environment.NewLine, Lines.ToArray());
            }
        }
    }

    /// <summary>
    /// In-memory list of everything tagged this session.
    /// </summary>
    public sealed class TagStore
    {
        private static readonly TagStore _current = new TagStore();
        public static TagStore Current { get { return _current; } }

        private readonly List<TagRecord> _records = new List<TagRecord>();
        private readonly object _gate = new object();

        private TagStore() { }

        public event EventHandler Changed;

        public int Count
        {
            get { lock (_gate) { return _records.Count; } }
        }

        public void Add(TagRecord record)
        {
            if (record == null) return;

            lock (_gate)
            {
                _records.Add(record);
            }

            Raise();
        }

        public List<TagRecord> Snapshot()
        {
            lock (_gate)
            {
                return new List<TagRecord>(_records);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                if (_records.Count == 0) return;
                _records.Clear();
            }

            Raise();
        }

        /// <summary>
        /// Drops everything added after the first <paramref name="count"/> records.
        /// The self test uses this to take its own tags back out without touching
        /// the ones the user placed.
        /// </summary>
        internal void TrimTo(int count)
        {
            if (count < 0) count = 0;

            lock (_gate)
            {
                if (_records.Count <= count) return;
                _records.RemoveRange(count, _records.Count - count);
            }

            Raise();
        }

        private void Raise()
        {
            EventHandler h = Changed;
            if (h != null) h(this, EventArgs.Empty);
        }
    }
}
