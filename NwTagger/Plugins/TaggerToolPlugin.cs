using System;
using System.Collections.Generic;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;
using NwTagger.Core;

namespace NwTagger.Plugins
{
    /// <summary>
    /// The interactive tagging tool.
    ///
    /// Click 1 picks the element and reads its quick properties.
    /// Click 2 places the text; a leader arrow is drawn back to the element and
    /// the whole thing is written into a saved viewpoint.
    ///
    /// While waiting for the second click the pending leader is drawn as an
    /// overlay so the user can see what they are about to get.
    /// </summary>
    [Plugin("NwTagger.Tool", "ABHM",
        DisplayName = "Element Tagger Tool",
        ToolTip = "Click an element, then click where the tag text should go.")]
    public sealed class TaggerToolPlugin : ToolPlugin
    {
        private const ushort LeftButton = 1;

        private PendingTag _pending;
        private int _cursorX;
        private int _cursorY;
        private bool _hasCursor;

        /// <summary>Set by the dock pane so it can show status messages.</summary>
        public static Action<string> StatusReporter;

        private static void Report(string message)
        {
            Action<string> reporter = StatusReporter;
            if (reporter != null)
            {
                try { reporter(message); } catch { /* UI is optional */ }
            }
        }

        /// <summary>Drops any half-finished tag. Called when the tool is disabled.</summary>
        public void CancelPending()
        {
            _pending = null;
            _hasCursor = false;
        }

        public override bool MouseDown(View view, KeyModifiers modifiers, ushort button, int x, int y, double time)
        {
            // Let Navisworks keep its own middle/right button behaviour.
            if (button != LeftButton) return false;
            if (view == null) return false;

            Document doc = view.Document;
            if (doc == null || doc.IsClear) return false;

            try
            {
                return _pending == null
                    ? HandleElementClick(view, doc, x, y)
                    : HandlePlacementClick(view, x, y);
            }
            catch (Exception ex)
            {
                CancelPending();
                Report("Tag failed: " + ex.Message);
                return true;
            }
        }

        /// <summary>First click - identify the element and read its quick properties.</summary>
        private bool HandleElementClick(View view, Document doc, int x, int y)
        {
            PickItemResult pick = view.PickItemFromPoint(x, y);

            if (pick == null || pick.ModelItem == null)
            {
                Report("Nothing under the cursor - click on an element.");
                return true;
            }

            ModelItem item = pick.ModelItem;

            // Highlight what was picked so the user gets immediate feedback.
            ModelItemCollection selection = new ModelItemCollection();
            selection.Add(item);
            doc.CurrentSelection.CopyFrom(selection);

            if (!TaggerSettings.Current.WriteDataOnClick)
            {
                Report("Selected: " + Safe(item) + "  (Write data on click is off - no tag written)");
                return true;
            }

            _pending = TagService.BeginTag(view, item, pick.Point, x, y);

            if (_pending.Lines.Count == 0)
            {
                _pending = null;
                Report("No quick properties are defined for that element.");
                return true;
            }

            _cursorX = x;
            _cursorY = y;
            _hasCursor = true;

            Report("Now click where the tag text should go.  (Esc cancels)");
            view.RequestDelayedRedraw(ViewRedrawRequests.OverlayRender);

            return true;
        }

        /// <summary>Second click - place the text and write the viewpoint.</summary>
        private bool HandlePlacementClick(View view, int x, int y)
        {
            PendingTag pending = _pending;
            _pending = null;
            _hasCursor = false;

            TagRecord record = TagService.CompleteTag(view, pending, x, y);

            if (record == null)
            {
                Report("Could not create the tag.");
            }
            else if (!string.IsNullOrEmpty(record.Warning))
            {
                // The markup did not survive the first write. Say so - a lost tag
                // should never pass as a success.
                Report(record.Warning);
            }
            else
            {
                Report(record.ReusedViewpoint
                    ? record.TagName + " added to the current viewpoint, now \"" + record.ViewpointName + "\""
                    : record.TagName + " saved as viewpoint \"" + record.ViewpointName + "\"");
            }

            view.RequestDelayedRedraw(ViewRedrawRequests.All);

            return true;
        }

        public override bool MouseMove(View view, KeyModifiers modifiers, int x, int y, double time)
        {
            if (_pending == null) return false;

            _cursorX = x;
            _cursorY = y;
            _hasCursor = true;

            if (view != null) view.RequestDelayedRedraw(ViewRedrawRequests.OverlayRender);

            return false;
        }

        public override bool KeyDown(View view, KeyModifiers modifiers, ushort key, double time)
        {
            const ushort VkEscape = 0x1B;

            if (key == VkEscape && _pending != null)
            {
                CancelPending();
                Report("Tag cancelled.");
                if (view != null) view.RequestDelayedRedraw(ViewRedrawRequests.All);
                return true;
            }

            return false;
        }

        public override Cursor GetCursor(View view, KeyModifiers modifiers)
        {
            return _pending == null ? Cursor.MarkupQuickPick : Cursor.Redline;
        }

        /// <summary>
        /// Draws the pending leader and a ghost of the text block.
        ///
        /// Window context uses the same top-left pixel origin as the mouse
        /// coordinates, so no Y flip is applied - see <see cref="ToWindow"/>.
        /// </summary>
        public override void OverlayRender(View view, Autodesk.Navisworks.Api.Graphics graphics)
        {
            PendingTag pending = _pending;
            if (pending == null || !_hasCursor || view == null || graphics == null) return;

            TaggerSettings settings = TaggerSettings.Current;

            try
            {
                int anchorX, anchorY;
                TagService.ResolveAnchor(view, pending, out anchorX, out anchorY);

                int textX, textY;
                TagService.ResolveTextPosition(view, pending, anchorX, anchorY, _cursorX, _cursorY, out textX, out textY);

                int attachX, attachY;
                TagService.ResolveAttachPoint(pending, anchorX, anchorY, textX, textY, out attachX, out attachY);

                graphics.BeginWindowContext();
                graphics.Color(RedlineBuilder.ToNavisworksColour(settings.TextColor), 1.0);
                graphics.LineWidth(settings.LeaderWidth);

                // Leader preview, leaving the edge of the block that faces the element.
                graphics.Line(ToWindow(graphics, attachX, attachY), ToWindow(graphics, anchorX, anchorY));

                // Small square on the element being tagged.
                const int marker = 4;
                graphics.Rectangle(
                    ToWindow(graphics, anchorX - marker, anchorY - marker),
                    ToWindow(graphics, anchorX + marker, anchorY + marker),
                    false);

                DrawPreviewText(graphics, pending, settings, textX, textY);

                graphics.EndWindowContext();
            }
            catch
            {
                // The preview is cosmetic - never let it break rendering.
            }
        }

        private static void DrawPreviewText(
            Autodesk.Navisworks.Api.Graphics graphics,
            PendingTag pending,
            TaggerSettings settings,
            int textX, int textY)
        {
            TextFontInfo font = new TextFontInfo(settings.FontName, settings.TextSize, 400, false, false);
            int lineHeight = TextLayout.LineHeight(settings.TextSize);

            for (int i = 0; i < pending.Lines.Count; i++)
            {
                Point2D position = ToWindow(graphics, textX, textY + lineHeight * i);
                graphics.Text2D(font, pending.Lines[i], position, 0, 0);
            }
        }

        /// <summary>
        /// Window context shares the mouse coordinate system: pixels, origin at
        /// the top left. Drawing the preview flipped made it track the cursor
        /// vertically inverted, so the coordinates pass straight through.
        /// </summary>
        private static Point2D ToWindow(Autodesk.Navisworks.Api.Graphics graphics, int x, int y)
        {
            return new Point2D(x, y);
        }

        private static string Safe(ModelItem item)
        {
            try { return string.IsNullOrEmpty(item.DisplayName) ? "(unnamed)" : item.DisplayName; }
            catch { return "(unnamed)"; }
        }
    }
}
