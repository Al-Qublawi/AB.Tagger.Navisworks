using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// This file is GDI+ heavy, and Autodesk.Navisworks.Api also defines Color and
// Graphics. Aliasing the handful of Navisworks types keeps every bare Color,
// Graphics, Font and Pen below unambiguously System.Drawing.
using Document = Autodesk.Navisworks.Api.Document;
using ImageGenerationStyle = Autodesk.Navisworks.Api.ImageGenerationStyle;
using SavedItem = Autodesk.Navisworks.Api.SavedItem;
using View = Autodesk.Navisworks.Api.View;
using Viewpoint = Autodesk.Navisworks.Api.Viewpoint;

namespace NwTagger.Core
{
    /// <summary>
    /// Renders a photo of each tagged viewpoint for the Excel export.
    ///
    /// The scene is captured with <see cref="ImageGenerationStyle.Scene"/> - the
    /// geometry only, deliberately without Navisworks' own overlay - and the tag
    /// markup is then drawn on top with GDI+ from the geometry stored on each
    /// <see cref="TagRecord"/>. Doing the markup ourselves means the photo always
    /// shows exactly one copy of the tag, whatever the viewer's overlay settings
    /// happen to be, and it looks the same at export resolution as on screen.
    /// </summary>
    public static class ViewpointPhotoService
    {
        /// <summary>Width of the exported photo in pixels.</summary>
        public const int PhotoWidth = 520;

        /// <summary>
        /// Captures one photo per viewpoint referenced by the records.
        /// The user's current view is restored before returning.
        /// </summary>
        /// <param name="progress">Called with (completed, total) as it goes.</param>
        public static Dictionary<Guid, byte[]> CapturePhotos(
            Document doc,
            IList<TagRecord> records,
            Action<int, int> progress)
        {
            Dictionary<Guid, byte[]> photos = new Dictionary<Guid, byte[]>();
            if (doc == null || doc.IsClear || records == null || records.Count == 0) return photos;

            View view = doc.ActiveView;
            if (view == null) return photos;

            // Group tags by the viewpoint they live in - several tags placed
            // without moving the camera share one photo.
            Dictionary<Guid, List<TagRecord>> groups = GroupByViewpoint(records);

            Viewpoint original = null;
            try { original = doc.CurrentViewpoint.CreateCopy(); }
            catch { original = null; }

            int done = 0;

            try
            {
                foreach (KeyValuePair<Guid, List<TagRecord>> group in groups)
                {
                    if (progress != null) progress(done, groups.Count);
                    done++;

                    byte[] png = CaptureOne(doc, view, group.Key, group.Value);
                    if (png != null) photos[group.Key] = png;
                }
            }
            finally
            {
                if (original != null)
                {
                    try { doc.CurrentViewpoint.CopyFrom(original); }
                    catch { /* best effort */ }
                }

                if (progress != null) progress(groups.Count, groups.Count);
            }

            return photos;
        }

        private static Dictionary<Guid, List<TagRecord>> GroupByViewpoint(IList<TagRecord> records)
        {
            Dictionary<Guid, List<TagRecord>> groups = new Dictionary<Guid, List<TagRecord>>();

            foreach (TagRecord record in records)
            {
                if (record.ViewpointGuid == Guid.Empty) continue;

                List<TagRecord> list;
                if (!groups.TryGetValue(record.ViewpointGuid, out list))
                {
                    list = new List<TagRecord>();
                    groups[record.ViewpointGuid] = list;
                }

                list.Add(record);
            }

            return groups;
        }

        private static byte[] CaptureOne(Document doc, View view, Guid viewpointGuid, List<TagRecord> tags)
        {
            Bitmap scene = null;

            try
            {
                SavedItem item = doc.SavedViewpoints.ResolveGuid(viewpointGuid);
                if (item == null) return null;

                // Move the camera to the tagged viewpoint.
                doc.SavedViewpoints.CurrentSavedViewpoint = item;

                double aspect = tags.Count > 0 && tags[0].ViewAspect > 0.01 ? tags[0].ViewAspect : 16.0 / 9.0;
                int height = (int)Math.Round(PhotoWidth / aspect);
                if (height < 80) height = 80;
                if (height > 1200) height = 1200;

                scene = view.GenerateImage(ImageGenerationStyle.Scene, PhotoWidth, height, true);
                if (scene == null) return null;

                DrawMarkup(scene, tags);

                using (MemoryStream stream = new MemoryStream())
                {
                    scene.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (scene != null) scene.Dispose();
            }
        }

        /// <summary>Redraws every tag belonging to this viewpoint onto the photo.</summary>
        private static void DrawMarkup(Bitmap bitmap, List<TagRecord> tags)
        {
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                foreach (TagRecord tag in tags)
                    DrawOne(g, bitmap.Width, bitmap.Height, tag);
            }
        }

        private static void DrawOne(Graphics g, int imageWidth, int imageHeight, TagRecord tag)
        {
            if (tag.Lines == null || tag.Lines.Count == 0) return;

            // Scale everything by how much the photo shrank relative to the view
            // the tag was placed in.
            double scale = tag.SourceViewHeight > 0 ? imageHeight / (double)tag.SourceViewHeight : 1.0;
            if (scale <= 0.05) scale = 0.05;

            float anchorX = (float)(tag.AnchorU * imageWidth);
            float anchorY = (float)(tag.AnchorV * imageHeight);
            float textX = (float)(tag.TextU * imageWidth);
            float textY = (float)(tag.TextV * imageHeight);
            float attachX = (float)(tag.AttachU * imageWidth);
            float attachY = (float)(tag.AttachV * imageHeight);

            Color colour = Color.FromArgb(tag.ColorArgb);
            float penWidth = Math.Max(1f, (float)(tag.LeaderWidth * scale));

            // Leader with an arrow head landing on the element.
            using (Pen pen = new Pen(colour, penWidth))
            {
                try
                {
                    pen.CustomEndCap = new AdjustableArrowCap(3.5f, 4.5f, true);
                }
                catch
                {
                    pen.EndCap = LineCap.ArrowAnchor;
                }

                g.DrawLine(pen, attachX, attachY, anchorX, anchorY);
            }

            // Text block.
            float fontPx = (float)(tag.TextSize * scale * 1.333); // points -> pixels at 96 dpi
            if (fontPx < 6f) fontPx = 6f;

            float lineHeight = (float)(TextLayout.LineHeight(tag.TextSize) * scale);
            if (lineHeight < fontPx) lineHeight = fontPx * 1.2f;

            using (Font font = CreateFont(fontPx))
            using (SolidBrush brush = new SolidBrush(colour))
            using (SolidBrush halo = new SolidBrush(Color.FromArgb(140, Color.White)))
            {
                for (int i = 0; i < tag.Lines.Count; i++)
                {
                    string line = tag.Lines[i];
                    if (string.IsNullOrEmpty(line)) continue;

                    float y = textY + lineHeight * i;

                    // A soft light plate keeps coloured text readable over the model.
                    SizeF size = g.MeasureString(line, font);
                    g.FillRectangle(halo, textX - 1f, y - 1f, size.Width + 2f, size.Height + 2f);

                    g.DrawString(line, font, brush, textX, y);
                }
            }
        }

        private static Font CreateFont(float pixelSize)
        {
            string requested = TaggerSettings.Current.FontName;

            try
            {
                return new Font(requested, pixelSize, FontStyle.Regular, GraphicsUnit.Pixel);
            }
            catch
            {
                return new Font(FontFamily.GenericSansSerif, pixelSize, FontStyle.Regular, GraphicsUnit.Pixel);
            }
        }
    }
}
