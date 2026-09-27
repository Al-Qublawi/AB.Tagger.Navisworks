using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;
using NwApplication = Autodesk.Navisworks.Api.Application;
using NwDocument = Autodesk.Navisworks.Api.Document;

namespace NwTagger.Core
{
    /// <summary>
    /// The whole "export the tag list" flow in one place, so the dock panel
    /// button and the ribbon command behave identically.
    /// </summary>
    public static class TagExportRunner
    {
        /// <summary>
        /// Prompts for a file, photographs the tagged viewpoints and writes the
        /// workbook. Returns false when there was nothing to do or the user
        /// cancelled.
        /// </summary>
        /// <param name="owner">Dialog owner; may be null.</param>
        /// <param name="status">Receives progress and result messages.</param>
        public static bool Run(IWin32Window owner, Action<string> status)
        {
            if (status == null) status = delegate { };

            List<TagRecord> records = TagStore.Current.Snapshot();

            if (records.Count == 0)
            {
                status("There are no tags to export yet.");
                return false;
            }

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "Export tags";
                dialog.Filter = "Excel workbook (*.xlsx)|*.xlsx|CSV file (*.csv)|*.csv";
                dialog.DefaultExt = "xlsx";
                dialog.FileName = "NavisworksTags_"
                    + DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture) + ".xlsx";

                DialogResult answer = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
                if (answer != DialogResult.OK) return false;

                bool wantsPhotos = !dialog.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

                try
                {
                    Dictionary<Guid, byte[]> photos = new Dictionary<Guid, byte[]>();

                    if (wantsPhotos)
                    {
                        NwDocument doc = NwApplication.ActiveDocument;

                        if (doc == null || doc.IsClear)
                        {
                            status("No model is open - exporting without photos.");
                        }
                        else
                        {
                            // Photographing steps the camera through each tagged
                            // viewpoint, then puts the original view back.
                            photos = ViewpointPhotoService.CapturePhotos(doc, records,
                                delegate (int done, int total)
                                {
                                    status("Photographing viewpoint " + Math.Min(done + 1, total) + " of " + total + "...");
                                    Application.DoEvents();
                                });
                        }
                    }

                    // One row per viewpoint, so rows can be fewer than tags.
                    int rows = TagExporter.Export(dialog.FileName, records, photos);

                    status("Exported " + records.Count + " tag(s) in " + rows + " viewpoint row(s), "
                         + photos.Count + " photo(s), to " + dialog.FileName);

                    return true;
                }
                catch (Exception ex)
                {
                    status("Export failed: " + ex.Message);
                    return false;
                }
            }
        }
    }
}
