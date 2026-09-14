using System;
using System.Drawing;

namespace NwTagger.Core
{
    /// <summary>
    /// Single global settings object shared by the dock pane and the tool.
    /// The pane writes to it, the tool reads from it, and anything interested
    /// listens to <see cref="Changed"/>.
    /// </summary>
    public sealed class TaggerSettings
    {
        private static readonly TaggerSettings _current = new TaggerSettings();
        public static TaggerSettings Current { get { return _current; } }

        private TaggerSettings() { }

        // ---- Text settings (mirrored into Navisworks' own redline options) ----
        private int _textSize = 14;
        private string _fontName = "Tahoma";
        private Color _textColor = Color.Red;
        private int _leaderWidth = 2;

        // ---- Behaviour ----
        private bool _writeDataOnClick = true;
        private bool _addCategoryTitle = true;
        private bool _includeItemName = true;
        private bool _splitText = true;
        private int _splitLength = 40;

        // ---- Runtime state ----
        private bool _isEnabled;
        private bool _tagsVisible = true;

        public event EventHandler Changed;

        private void Raise()
        {
            EventHandler h = Changed;
            if (h != null) h(this, EventArgs.Empty);
        }

        /// <summary>Redline text point size. Navisworks applies this globally.</summary>
        public int TextSize
        {
            get { return _textSize; }
            set
            {
                int v = Clamp(value, 6, 96);
                if (v == _textSize) return;
                _textSize = v; Raise();
            }
        }

        /// <summary>Redline typeface name. Navisworks applies this globally.</summary>
        public string FontName
        {
            get { return _fontName; }
            set
            {
                string v = string.IsNullOrEmpty(value) ? "Tahoma" : value;
                if (v == _fontName) return;
                _fontName = v; Raise();
            }
        }

        /// <summary>Tag colour. Applied per redline object, so it can vary per tag.</summary>
        public Color TextColor
        {
            get { return _textColor; }
            set { if (value == _textColor) return; _textColor = value; Raise(); }
        }

        /// <summary>Leader line thickness in pixels.</summary>
        public int LeaderWidth
        {
            get { return _leaderWidth; }
            set
            {
                int v = Clamp(value, 1, 10);
                if (v == _leaderWidth) return;
                _leaderWidth = v; Raise();
            }
        }

        /// <summary>
        /// When true, clicking an element immediately writes the tag text.
        /// When false, the element is only selected and highlighted; nothing is written.
        /// </summary>
        public bool WriteDataOnClick
        {
            get { return _writeDataOnClick; }
            set { if (value == _writeDataOnClick) return; _writeDataOnClick = value; Raise(); }
        }

        /// <summary>
        /// Show "Category: Property" instead of just the value.
        /// Maps onto Navisworks' interface.smart_tags.hide_category option (inverted).
        /// </summary>
        public bool AddCategoryTitle
        {
            get { return _addCategoryTitle; }
            set { if (value == _addCategoryTitle) return; _addCategoryTitle = value; Raise(); }
        }

        /// <summary>Prefix the tag with "Item Name: {DisplayName}".</summary>
        public bool IncludeItemName
        {
            get { return _includeItemName; }
            set { if (value == _includeItemName) return; _includeItemName = value; Raise(); }
        }

        /// <summary>Wrap long property lines across multiple redline text objects.</summary>
        public bool SplitText
        {
            get { return _splitText; }
            set { if (value == _splitText) return; _splitText = value; Raise(); }
        }

        /// <summary>Characters per line when <see cref="SplitText"/> is on.</summary>
        public int SplitLength
        {
            get { return _splitLength; }
            set
            {
                int v = Clamp(value, 8, 200);
                if (v == _splitLength) return;
                _splitLength = v; Raise();
            }
        }

        /// <summary>True while the tagging tool is the active Navisworks tool.</summary>
        public bool IsEnabled
        {
            get { return _isEnabled; }
            set { if (value == _isEnabled) return; _isEnabled = value; Raise(); }
        }

        /// <summary>Session flag used by the "show / hide tags" toggle.</summary>
        public bool TagsVisible
        {
            get { return _tagsVisible; }
            set { if (value == _tagsVisible) return; _tagsVisible = value; Raise(); }
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
