using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;
using NwTagger.Core;
using NwTagger.Plugins;
using NwApplication = Autodesk.Navisworks.Api.Application;
using NwDocument = Autodesk.Navisworks.Api.Document;
using NwView = Autodesk.Navisworks.Api.View;

namespace NwTagger.UI
{
    /// <summary>
    /// The tagger settings panel. Everything here writes into
    /// <see cref="TaggerSettings.Current"/>, which the tool reads on each click.
    /// </summary>
    public sealed class TaggerPaneControl : UserControl
    {
        /// <summary>Author profile opened by the footer link.</summary>
        private const string LinkedInUrl = "https://www.linkedin.com/in/abdullahalqublawi/";

        private const string LinkedInCaption = "Abdullah Lotfy - LinkedIn";

        private readonly TaggerSettings _settings = TaggerSettings.Current;

        private NumericUpDown _size;
        private ComboBox _font;
        private TextBox _colorText;
        private Button _colorSwatch;
        private NumericUpDown _leaderWidth;

        private CheckBox _writeOnClick;
        private CheckBox _addCategory;
        private CheckBox _includeName;

        private CheckBox _splitText;
        private NumericUpDown _splitLength;

        private Button _enable;
        private Button _disable;

        private Label _tagCount;
        private Button _export;
        private Button _clear;
        private Button _toggleVisibility;

        private Label _status;
        private LinkLabel _linkedIn;
        private Image _logo;
        private Timer _stateTimer;

        private bool _suppressEvents;

        // Baseline redline state used by the show/hide toggle.
        private string _hiddenRedlineBaseline;
        private string _cachedRedlines;
        private bool _tagsHidden;

        public TaggerPaneControl()
        {
            BuildLayout();
            LoadFromSettings();

            TaggerToolPlugin.StatusReporter = ReportStatus;
            TagStore.Current.Changed += OnTagStoreChanged;

            _stateTimer = new Timer();
            _stateTimer.Interval = 500;
            _stateTimer.Tick += delegate { RefreshEnabledState(); };
            _stateTimer.Start();

            RefreshEnabledState();
            UpdateTagCount();
        }

        // ------------------------------------------------------------ layout

        private void BuildLayout()
        {
            SuspendLayout();

            BackColor = SystemColors.Control;
            Padding = new Padding(8);
            AutoScroll = true;
            MinimumSize = new Size(280, 400);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Top;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            root.Controls.Add(BuildHeader());
            root.Controls.Add(BuildTextSettingsGroup());
            root.Controls.Add(BuildMultilineGroup());
            root.Controls.Add(BuildActionButtons());
            root.Controls.Add(BuildTagsGroup());
            root.Controls.Add(BuildStatus());
            root.Controls.Add(BuildFooter());

            for (int i = 0; i < root.Controls.Count; i++)
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.RowCount = root.Controls.Count;

            Controls.Add(root);
            ResumeLayout(false);
        }

        /// <summary>Product banner: the logo alongside the add-in name.</summary>
        private Control BuildHeader()
        {
            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 52;
            header.Margin = new Padding(0, 0, 0, 8);
            header.BackColor = Color.FromArgb(23, 34, 48);

            _logo = LoadLogo();

            if (_logo != null)
            {
                PictureBox picture = new PictureBox();
                picture.Image = _logo;
                picture.SizeMode = PictureBoxSizeMode.Zoom;
                picture.Location = new Point(8, 6);
                picture.Size = new Size(40, 40);
                picture.BackColor = Color.Transparent;
                header.Controls.Add(picture);
            }

            Label title = new Label();
            title.Text = "AB Tagger";
            title.ForeColor = Color.White;
            title.Font = new Font(SystemFonts.DefaultFont.FontFamily, 12f, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(_logo == null ? 10 : 56, 7);
            header.Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = "Quick-property element tagging";
            subtitle.ForeColor = Color.FromArgb(150, 175, 205);
            subtitle.AutoSize = true;
            subtitle.Location = new Point(_logo == null ? 12 : 58, 30);
            header.Controls.Add(subtitle);

            return header;
        }

        /// <summary>
        /// The logo ships inside the assembly, so it cannot go missing the way a
        /// loose file beside the DLL could.
        /// </summary>
        private static Image LoadLogo()
        {
            try
            {
                using (System.IO.Stream stream = typeof(TaggerPaneControl).Assembly
                    .GetManifestResourceStream("NwTagger.Images.logo.png"))
                {
                    if (stream == null) return null;
                    return Image.FromStream(stream);
                }
            }
            catch
            {
                return null;
            }
        }

        private GroupBox BuildTextSettingsGroup()
        {
            GroupBox group = new GroupBox();
            group.Text = "Text Settings";
            group.Dock = DockStyle.Top;
            group.AutoSize = true;
            group.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            group.Padding = new Padding(8, 4, 8, 8);
            group.Margin = new Padding(0, 0, 0, 8);

            TableLayoutPanel table = NewTable(2);

            // Size
            table.Controls.Add(NewLabel("Size"), 0, 0);
            _size = NewNumeric(6, 96, 1);
            _size.ValueChanged += delegate
            {
                if (_suppressEvents) return;
                _settings.TextSize = (int)_size.Value;
                ApplyTextSettings();
            };
            table.Controls.Add(_size, 1, 0);

            // Font
            table.Controls.Add(NewLabel("Font"), 0, 1);
            _font = new ComboBox();
            _font.Dock = DockStyle.Fill;
            _font.DropDownStyle = ComboBoxStyle.DropDownList;
            _font.Items.AddRange(InstalledFontNames());
            _font.SelectedIndexChanged += delegate
            {
                if (_suppressEvents) return;
                _settings.FontName = _font.SelectedItem as string;
                ApplyTextSettings();
            };
            table.Controls.Add(_font, 1, 1);

            // Colour
            table.Controls.Add(NewLabel("Color"), 0, 2);

            TableLayoutPanel colorRow = new TableLayoutPanel();
            colorRow.Dock = DockStyle.Fill;
            colorRow.AutoSize = true;
            colorRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            colorRow.ColumnCount = 3;
            colorRow.Margin = new Padding(0);
            colorRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            colorRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34f));
            colorRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30f));

            _colorText = new TextBox();
            _colorText.Dock = DockStyle.Fill;
            _colorText.ReadOnly = true;
            _colorText.BackColor = SystemColors.Control;
            colorRow.Controls.Add(_colorText, 0, 0);

            _colorSwatch = new Button();
            _colorSwatch.Dock = DockStyle.Fill;
            _colorSwatch.Height = 22;
            _colorSwatch.FlatStyle = FlatStyle.Flat;
            _colorSwatch.Click += delegate { PickColor(); };
            colorRow.Controls.Add(_colorSwatch, 1, 0);

            Button browse = new Button();
            browse.Dock = DockStyle.Fill;
            browse.Text = "...";
            browse.Click += delegate { PickColor(); };
            colorRow.Controls.Add(browse, 2, 0);

            table.Controls.Add(colorRow, 1, 2);

            // Leader width
            table.Controls.Add(NewLabel("Leader"), 0, 3);
            _leaderWidth = NewNumeric(1, 10, 1);
            _leaderWidth.ValueChanged += delegate
            {
                if (_suppressEvents) return;
                _settings.LeaderWidth = (int)_leaderWidth.Value;
                ApplyTextSettings();
            };
            table.Controls.Add(_leaderWidth, 1, 3);

            // Checkboxes span both columns.
            _writeOnClick = NewCheck("Write data on click");
            _writeOnClick.CheckedChanged += delegate
            {
                if (_suppressEvents) return;
                _settings.WriteDataOnClick = _writeOnClick.Checked;
            };
            table.Controls.Add(_writeOnClick, 0, 4);
            table.SetColumnSpan(_writeOnClick, 2);

            _addCategory = NewCheck("Add category title");
            _addCategory.CheckedChanged += delegate
            {
                if (_suppressEvents) return;
                _settings.AddCategoryTitle = _addCategory.Checked;
                ApplyTextSettings();
            };
            table.Controls.Add(_addCategory, 0, 5);
            table.SetColumnSpan(_addCategory, 2);

            _includeName = NewCheck("Add \"Item Name\" header line");
            _includeName.CheckedChanged += delegate
            {
                if (_suppressEvents) return;
                _settings.IncludeItemName = _includeName.Checked;
            };
            table.Controls.Add(_includeName, 0, 6);
            table.SetColumnSpan(_includeName, 2);

            Label note = new Label();
            note.Text = "Font and size are Navisworks redline options, so they apply "
                      + "to every redline in the document. Colour is per tag.";
            note.Dock = DockStyle.Fill;
            note.AutoSize = true;
            note.MaximumSize = new Size(260, 0);
            note.ForeColor = SystemColors.GrayText;
            note.Margin = new Padding(3, 6, 3, 0);
            table.Controls.Add(note, 0, 7);
            table.SetColumnSpan(note, 2);

            table.RowCount = 8;
            group.Controls.Add(table);

            return group;
        }

        private GroupBox BuildMultilineGroup()
        {
            GroupBox group = new GroupBox();
            group.Text = "Multiline Text";
            group.Dock = DockStyle.Top;
            group.AutoSize = true;
            group.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            group.Padding = new Padding(8, 4, 8, 8);
            group.Margin = new Padding(0, 0, 0, 8);

            TableLayoutPanel table = NewTable(2);

            _splitText = NewCheck("Divide text in multiple lines");
            _splitText.CheckedChanged += delegate
            {
                if (_suppressEvents) return;
                _settings.SplitText = _splitText.Checked;
                _splitLength.Enabled = _splitText.Checked;
            };
            table.Controls.Add(_splitText, 0, 0);
            table.SetColumnSpan(_splitText, 2);

            _splitLength = NewNumeric(8, 200, 1);
            _splitLength.ValueChanged += delegate
            {
                if (_suppressEvents) return;
                _settings.SplitLength = (int)_splitLength.Value;
            };
            table.Controls.Add(_splitLength, 0, 1);

            Label chars = new Label();
            chars.Text = "Characters per line";
            chars.Dock = DockStyle.Fill;
            chars.AutoSize = true;
            chars.TextAlign = ContentAlignment.MiddleLeft;
            table.Controls.Add(chars, 1, 1);

            table.RowCount = 2;
            group.Controls.Add(table);

            return group;
        }

        private Control BuildActionButtons()
        {
            TableLayoutPanel panel = new TableLayoutPanel();
            panel.Dock = DockStyle.Top;
            panel.AutoSize = true;
            panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            panel.ColumnCount = 2;
            panel.RowCount = 1;
            panel.Margin = new Padding(0, 0, 0, 8);
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

            _enable = NewActionButton("ENABLE", Color.FromArgb(0, 176, 80));
            _enable.Click += delegate { OnEnable(); };
            panel.Controls.Add(_enable, 0, 0);

            _disable = NewActionButton("DISABLE", Color.FromArgb(224, 32, 32));
            _disable.Click += delegate { OnDisable(); };
            panel.Controls.Add(_disable, 1, 0);

            return panel;
        }

        private GroupBox BuildTagsGroup()
        {
            GroupBox group = new GroupBox();
            group.Text = "Tags";
            group.Dock = DockStyle.Top;
            group.AutoSize = true;
            group.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            group.Padding = new Padding(8, 4, 8, 8);
            group.Margin = new Padding(0, 0, 0, 8);

            TableLayoutPanel table = NewTable(1);

            _tagCount = new Label();
            _tagCount.Dock = DockStyle.Fill;
            _tagCount.AutoSize = true;
            table.Controls.Add(_tagCount, 0, 0);

            _toggleVisibility = new Button();
            _toggleVisibility.Text = "Hide tags in current view";
            _toggleVisibility.Dock = DockStyle.Fill;
            _toggleVisibility.Height = 26;
            _toggleVisibility.Click += delegate { ToggleTagVisibility(); };
            table.Controls.Add(_toggleVisibility, 0, 1);

            _export = new Button();
            _export.Text = "Export tags to Excel...";
            _export.Dock = DockStyle.Fill;
            _export.Height = 26;
            _export.Click += delegate { ExportTags(); };
            table.Controls.Add(_export, 0, 2);

            _clear = new Button();
            _clear.Text = "Clear tag list";
            _clear.Dock = DockStyle.Fill;
            _clear.Height = 26;
            _clear.Click += delegate { ClearTags(); };
            table.Controls.Add(_clear, 0, 3);

            table.RowCount = 4;
            group.Controls.Add(table);

            return group;
        }

        private Control BuildStatus()
        {
            _status = new Label();
            _status.Dock = DockStyle.Top;
            _status.AutoSize = true;
            _status.MaximumSize = new Size(280, 0);
            _status.Padding = new Padding(2, 4, 2, 4);
            _status.ForeColor = SystemColors.ControlText;
            _status.Text = "Ready. Press ENABLE, click an element, then click where the text should go.";

            return _status;
        }

        private Control BuildFooter()
        {
            _linkedIn = new LinkLabel();
            _linkedIn.Text = LinkedInCaption;
            _linkedIn.Dock = DockStyle.Top;
            _linkedIn.AutoSize = false;
            _linkedIn.Height = 34;
            _linkedIn.TextAlign = ContentAlignment.MiddleCenter;
            _linkedIn.Font = new Font(SystemFonts.DefaultFont.FontFamily, 9.5f, FontStyle.Bold);
            _linkedIn.LinkColor = Color.FromArgb(10, 102, 194);   // LinkedIn blue
            _linkedIn.ActiveLinkColor = Color.FromArgb(0, 74, 143);
            _linkedIn.VisitedLinkColor = Color.FromArgb(10, 102, 194);
            _linkedIn.LinkBehavior = LinkBehavior.HoverUnderline;
            _linkedIn.Margin = new Padding(0, 6, 0, 4);
            _linkedIn.Cursor = Cursors.Hand;
            _linkedIn.LinkClicked += OnLinkedInClicked;

            return _linkedIn;
        }

        private void OnLinkedInClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            try
            {
                _linkedIn.LinkVisited = true;
                System.Diagnostics.Process.Start(LinkedInUrl);
            }
            catch (Exception ex)
            {
                ReportStatus("Could not open the browser (" + ex.Message + "). The address is " + LinkedInUrl);
            }
        }

        // ------------------------------------------------------- small helpers

        private static TableLayoutPanel NewTable(int columns)
        {
            TableLayoutPanel table = new TableLayoutPanel();
            table.Dock = DockStyle.Top;
            table.AutoSize = true;
            table.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            table.ColumnCount = columns;
            table.Margin = new Padding(0);

            if (columns == 2)
            {
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62f));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            }
            else
            {
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            }

            return table;
        }

        private static Label NewLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.AutoSize = true;
            label.Margin = new Padding(3, 6, 3, 3);
            return label;
        }

        private static NumericUpDown NewNumeric(int min, int max, int step)
        {
            NumericUpDown numeric = new NumericUpDown();
            numeric.Dock = DockStyle.Fill;
            numeric.Minimum = min;
            numeric.Maximum = max;
            numeric.Increment = step;
            return numeric;
        }

        private static CheckBox NewCheck(string text)
        {
            CheckBox check = new CheckBox();
            check.Text = text;
            check.Dock = DockStyle.Fill;
            check.AutoSize = true;
            check.Margin = new Padding(3, 4, 3, 0);
            return check;
        }

        private static Button NewActionButton(string text, Color colour)
        {
            Button button = new Button();
            button.Text = text;
            button.Dock = DockStyle.Fill;
            button.Height = 38;
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = colour;
            button.ForeColor = Color.White;
            button.Font = new Font(SystemFonts.DefaultFont.FontFamily, 10f, FontStyle.Bold);
            button.FlatAppearance.BorderSize = 0;
            button.Margin = new Padding(3);
            return button;
        }

        private static object[] InstalledFontNames()
        {
            List<object> names = new List<object>();

            try
            {
                using (InstalledFontCollection fonts = new InstalledFontCollection())
                {
                    foreach (FontFamily family in fonts.Families)
                        names.Add(family.Name);
                }
            }
            catch
            {
                names.Add("Tahoma");
                names.Add("Arial");
            }

            return names.ToArray();
        }

        // ------------------------------------------------------------ actions

        private void LoadFromSettings()
        {
            _suppressEvents = true;

            try
            {
                _size.Value = Clamp(_settings.TextSize, (int)_size.Minimum, (int)_size.Maximum);
                _leaderWidth.Value = Clamp(_settings.LeaderWidth, (int)_leaderWidth.Minimum, (int)_leaderWidth.Maximum);

                int index = _font.Items.IndexOf(_settings.FontName);
                if (index >= 0) _font.SelectedIndex = index;
                else if (_font.Items.Count > 0) _font.SelectedIndex = 0;

                _writeOnClick.Checked = _settings.WriteDataOnClick;
                _addCategory.Checked = _settings.AddCategoryTitle;
                _includeName.Checked = _settings.IncludeItemName;

                _splitText.Checked = _settings.SplitText;
                _splitLength.Value = Clamp(_settings.SplitLength, (int)_splitLength.Minimum, (int)_splitLength.Maximum);
                _splitLength.Enabled = _settings.SplitText;

                UpdateColorDisplay();
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private void UpdateColorDisplay()
        {
            Color c = _settings.TextColor;
            _colorText.Text = string.Format(CultureInfo.InvariantCulture, "{0:x2}{1:x2}{2:x2}{3:x2}", c.A, c.R, c.G, c.B);
            _colorSwatch.BackColor = c;
        }

        private void PickColor()
        {
            using (ColorDialog dialog = new ColorDialog())
            {
                dialog.Color = _settings.TextColor;
                dialog.FullOpen = true;
                dialog.AnyColor = true;

                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                _settings.TextColor = dialog.Color;
                UpdateColorDisplay();
                ApplyTextSettings();
            }
        }

        /// <summary>
        /// Pushes text settings into the Navisworks redline options and surfaces
        /// anything Navisworks refused, rather than failing silently.
        /// </summary>
        private void ApplyTextSettings()
        {
            string message;
            bool ok = GlobalOptionsBridge.ApplyTextSettings(_settings, out message);

            if (!ok && !string.IsNullOrEmpty(message))
                ReportStatus(message);
        }

        private void OnEnable()
        {
            string message;

            if (ToolController.Enable(out message))
                ReportStatus(string.IsNullOrEmpty(message)
                    ? "Tagger enabled. Click an element, then click where the text should go."
                    : message);
            else
                ReportStatus(message ?? "Could not enable the tagger.");

            RefreshEnabledState();
        }

        private void OnDisable()
        {
            ToolController.Disable();
            ReportStatus("Tagger disabled. Navisworks is back on the Select tool.");
            RefreshEnabledState();
        }

        private void RefreshEnabledState()
        {
            bool active = ToolController.IsActive;

            if (_settings.IsEnabled != active) _settings.IsEnabled = active;

            _enable.Enabled = !active;
            _disable.Enabled = active;

            _enable.BackColor = active ? Color.FromArgb(160, 205, 175) : Color.FromArgb(0, 176, 80);
            _disable.BackColor = active ? Color.FromArgb(224, 32, 32) : Color.FromArgb(225, 170, 170);
        }

        private void OnTagStoreChanged(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke((MethodInvoker)UpdateTagCount); }
                catch { /* control going away */ }
                return;
            }

            UpdateTagCount();
        }

        private void UpdateTagCount()
        {
            _tagCount.Text = "Tags this session: " + TagStore.Current.Count.ToString(CultureInfo.CurrentCulture);
        }

        private void ExportTags()
        {
            Cursor previousCursor = Cursor;
            Cursor = Cursors.WaitCursor;
            _export.Enabled = false;

            try
            {
                // Shared with the ribbon command so both behave identically.
                TagExportRunner.Run(this, ReportStatusImmediate);
            }
            finally
            {
                _export.Enabled = true;
                Cursor = previousCursor;
            }
        }

        /// <summary>
        /// Status updates that must paint straight away, so the panel stays
        /// readable while the camera walks the viewpoints during an export.
        /// </summary>
        private void ReportStatusImmediate(string message)
        {
            ReportStatus(message);
            _status.Refresh();
        }

        private void ClearTags()
        {
            DialogResult answer = MessageBox.Show(
                this,
                "Clear the session tag list?\n\nThis only clears the list in this panel. "
                    + "Saved viewpoints and their markup are not touched.",
                "Element Tagger",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer != DialogResult.Yes) return;

            TagStore.Current.Clear();
            ViewpointService.Current.Reset();
            ReportStatus("Tag list cleared. The next tag starts a new viewpoint.");
        }

        /// <summary>
        /// Hides or restores redlines in the live view only. Saved viewpoints keep
        /// their markup either way - this is a viewing aid, not an edit.
        /// </summary>
        private void ToggleTagVisibility()
        {
            NwDocument doc = NwApplication.ActiveDocument;

            if (doc == null || doc.IsClear)
            {
                ReportStatus("No model is open.");
                return;
            }

            NwView view = doc.ActiveView;
            if (view == null)
            {
                ReportStatus("No active view.");
                return;
            }

            try
            {
                if (!_tagsHidden)
                {
                    _cachedRedlines = view.GetRedlines();

                    if (string.IsNullOrEmpty(_hiddenRedlineBaseline))
                    {
                        ReportStatus("Cannot hide markup: no empty-redline baseline was captured for this view. "
                                   + "Enable the tagger once before the first tag to capture it.");
                        return;
                    }

                    if (!view.TrySetRedlines(_hiddenRedlineBaseline))
                    {
                        ReportStatus("Navisworks refused to hide the markup in this view.");
                        return;
                    }

                    _tagsHidden = true;
                    _toggleVisibility.Text = "Show tags in current view";
                    ReportStatus("Markup hidden in the current view. Saved viewpoints are unchanged.");
                }
                else
                {
                    if (!string.IsNullOrEmpty(_cachedRedlines)) view.TrySetRedlines(_cachedRedlines);

                    _tagsHidden = false;
                    _toggleVisibility.Text = "Hide tags in current view";
                    ReportStatus("Markup restored in the current view.");
                }

                _settings.TagsVisible = !_tagsHidden;
            }
            catch (Exception ex)
            {
                ReportStatus("Visibility toggle failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Captures what "no markup" looks like for this document, so the
        /// visibility toggle has something safe to restore to.
        /// </summary>
        internal void CaptureRedlineBaseline()
        {
            if (!string.IsNullOrEmpty(_hiddenRedlineBaseline)) return;

            try
            {
                NwDocument doc = NwApplication.ActiveDocument;
                if (doc == null || doc.IsClear) return;

                NwView view = doc.ActiveView;
                if (view == null) return;

                if (TagStore.Current.Count == 0)
                    _hiddenRedlineBaseline = view.GetRedlines();
            }
            catch
            {
                _hiddenRedlineBaseline = null;
            }
        }

        private void ReportStatus(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            if (InvokeRequired)
            {
                try { BeginInvoke((MethodInvoker)delegate { ReportStatus(message); }); }
                catch { /* control going away */ }
                return;
            }

            _status.Text = message;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            CaptureRedlineBaseline();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_stateTimer != null)
                {
                    _stateTimer.Stop();
                    _stateTimer.Dispose();
                    _stateTimer = null;
                }

                TagStore.Current.Changed -= OnTagStoreChanged;

                if (_logo != null)
                {
                    _logo.Dispose();
                    _logo = null;
                }

                if (TaggerToolPlugin.StatusReporter == (Action<string>)ReportStatus)
                    TaggerToolPlugin.StatusReporter = null;
            }

            base.Dispose(disposing);
        }
    }
}
