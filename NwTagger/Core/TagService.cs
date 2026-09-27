using System;
using System.Collections.Generic;
using Autodesk.Navisworks.Api;

namespace NwTagger.Core
{
    /// <summary>
    /// The element captured by the first click, held until the second click
    /// decides where its text goes.
    /// </summary>
    public sealed class PendingTag
    {
        public ModelItem Item { get; set; }
        public Point3D WorldPoint { get; set; }
        public int ClickX { get; set; }
        public int ClickY { get; set; }

        /// <summary>
        /// True when View.ProjectPoint reproduced the original click position,
        /// meaning we can safely re-project if the camera moves before placement.
        /// </summary>
        public bool ProjectionTrusted { get; set; }

        public List<string> Lines { get; set; }
        public string ElementId { get; set; }
    }

    /// <summary>
    /// Orchestrates a tag: read quick properties, lay the text out, build the
    /// redlines, and hand them to the viewpoint service.
    /// </summary>
    public static class TagService
    {
        /// <summary>
        /// Captures the picked element and pre-computes its text so the preview
        /// during placement shows the real content.
        /// </summary>
        public static PendingTag BeginTag(View view, ModelItem item, Point3D worldPoint, int clickX, int clickY)
        {
            TaggerSettings settings = TaggerSettings.Current;

            List<string> raw = QuickPropertyReader.BuildTagLines(item, settings);
            List<string> lines = TextLayout.Wrap(raw, settings.SplitText, settings.SplitLength);

            PendingTag pending = new PendingTag
            {
                Item = item,
                WorldPoint = worldPoint,
                ClickX = clickX,
                ClickY = clickY,
                Lines = lines,
                ElementId = ElementIdResolver.Resolve(item),
                ProjectionTrusted = CalibrateProjection(view, worldPoint, clickX, clickY)
            };

            return pending;
        }

        /// <summary>
        /// Checks whether View.ProjectPoint round-trips the click we just handled.
        /// If it does, the anchor can follow the element when the camera moves.
        /// If it does not, we fall back to the recorded pixel position.
        /// </summary>
        private static bool CalibrateProjection(View view, Point3D worldPoint, int clickX, int clickY)
        {
            try
            {
                ProjectionResult projected = view.ProjectPoint(worldPoint, false, false);
                if (projected == null) return false;

                int dx = Math.Abs(projected.X - clickX);
                int dy = Math.Abs(projected.Y - clickY);

                return dx <= 6 && dy <= 6;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Current screen position of the element the pending tag points at.
        /// </summary>
        public static void ResolveAnchor(View view, PendingTag pending, out int anchorX, out int anchorY)
        {
            anchorX = pending.ClickX;
            anchorY = pending.ClickY;

            if (!pending.ProjectionTrusted) return;

            try
            {
                ProjectionResult projected = view.ProjectPoint(pending.WorldPoint, false, false);
                if (projected == null) return;

                // Ignore obviously bogus results (element behind the camera, etc).
                if (projected.X < -5000 || projected.X > view.Width + 5000) return;
                if (projected.Y < -5000 || projected.Y > view.Height + 5000) return;

                anchorX = projected.X;
                anchorY = projected.Y;
            }
            catch
            {
                // keep the recorded click
            }
        }

        /// <summary>
        /// Computes the final text-block position for a placement click,
        /// applying the smart offset rules.
        /// </summary>
        public static void ResolveTextPosition(
            View view, PendingTag pending,
            int anchorX, int anchorY,
            int clickX, int clickY,
            out int textX, out int textY)
        {
            TaggerSettings settings = TaggerSettings.Current;

            textX = clickX;
            textY = clickY;

            int lineHeight = TextLayout.LineHeight(settings.TextSize);
            int blockWidth = TextLayout.EstimateWidth(pending.Lines, settings.TextSize);

            TextLayout.ApplySmartOffset(
                anchorX, anchorY,
                ref textX, ref textY,
                blockWidth, pending.Lines.Count, lineHeight,
                view.Width, view.Height);
        }

        /// <summary>
        /// Works out where the leader leaves the text block, so the preview, the
        /// saved markup and the exported photo all draw the same leader.
        /// </summary>
        public static void ResolveAttachPoint(
            PendingTag pending,
            int anchorX, int anchorY,
            int textX, int textY,
            out int attachX, out int attachY)
        {
            TaggerSettings settings = TaggerSettings.Current;

            int lineHeight = TextLayout.LineHeight(settings.TextSize);
            int blockWidth = TextLayout.EstimateWidth(pending.Lines, settings.TextSize);

            TextLayout.LeaderAttachPoint(
                textX, textY,
                blockWidth, pending.Lines.Count, lineHeight,
                anchorX, anchorY,
                out attachX, out attachY);
        }

        /// <summary>
        /// Writes the tag: creates the redlines, stores them in a saved viewpoint,
        /// and records the result. Returns null if nothing could be created.
        /// </summary>
        public static TagRecord CompleteTag(View view, PendingTag pending, int clickX, int clickY)
        {
            if (view == null || pending == null || pending.Lines == null || pending.Lines.Count == 0)
                return null;

            Document doc = view.Document;
            if (doc == null || doc.IsClear) return null;

            TaggerSettings settings = TaggerSettings.Current;

            int anchorX, anchorY;
            ResolveAnchor(view, pending, out anchorX, out anchorY);

            int textX, textY;
            ResolveTextPosition(view, pending, anchorX, anchorY, clickX, clickY, out textX, out textY);

            int attachX, attachY;
            ResolveAttachPoint(pending, anchorX, anchorY, textX, textY, out attachX, out attachY);

            List<MarkupItem> markup = RedlineBuilder.Build(
                view, pending.Lines, anchorX, anchorY, textX, textY, attachX, attachY, settings);

            if (markup.Count == 0) return null;

            ModelItemCollection selection = new ModelItemCollection();
            if (pending.Item != null) selection.Add(pending.Item);

            ViewpointResult viewpoint = ViewpointService.Current.AddTag(doc, view, markup, selection);

            double width = view.Width <= 0 ? 1.0 : view.Width;
            double height = view.Height <= 0 ? 1.0 : view.Height;

            TagRecord record = new TagRecord
            {
                CreatedUtc = DateTime.UtcNow,
                TagName = viewpoint.TagName,
                FileName = ResolveSourceFile(pending.Item),
                ViewpointGuid = viewpoint.ViewpointGuid,
                ViewpointName = viewpoint.ViewpointName,
                ReusedViewpoint = viewpoint.Reused,
                Warning = viewpoint.Warning,
                ElementId = pending.ElementId,
                DisplayName = pending.Item == null ? string.Empty : SafeName(pending.Item),
                Lines = new List<string>(pending.Lines),

                // Normalised so the exporter can redraw at any resolution.
                AnchorU = anchorX / width,
                AnchorV = anchorY / height,
                TextU = textX / width,
                TextV = textY / height,
                AttachU = attachX / width,
                AttachV = attachY / height,

                ColorArgb = settings.TextColor.ToArgb(),
                TextSize = settings.TextSize,
                LeaderWidth = settings.LeaderWidth,
                ViewAspect = width / height,
                SourceViewHeight = view.Height <= 0 ? 1 : view.Height
            };

            TagStore.Current.Add(record);

            return record;
        }

        private static string SafeName(ModelItem item)
        {
            try { return item.DisplayName ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string ResolveSourceFile(ModelItem item)
        {
            if (item == null) return string.Empty;

            try
            {
                foreach (ModelItem ancestor in item.AncestorsAndSelf)
                {
                    if (ancestor.HasModel && ancestor.Model != null)
                        return System.IO.Path.GetFileName(ancestor.Model.SourceFileName ?? string.Empty);
                }
            }
            catch
            {
                // not fatal
            }

            return string.Empty;
        }
    }
}
