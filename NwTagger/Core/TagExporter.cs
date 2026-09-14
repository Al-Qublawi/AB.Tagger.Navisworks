using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace NwTagger.Core
{
    /// <summary>
    /// Writes the session tag list to a real .xlsx with the saved-viewpoint photo
    /// embedded in each row, or to .csv when a photo-less list is enough.
    ///
    /// The workbook is assembled by hand - no external library - using inline
    /// strings for cell text and a spreadsheet drawing part for the images.
    /// </summary>
    public static class TagExporter
    {
        private const string HeaderTagName = "Tag Name";
        private const string HeaderFileName = "File Name";
        private const string HeaderPhoto = "Saved Viewpoint";
        private const string HeaderComments = "Comments";

        /// <summary>English Metric Units per pixel at 96 dpi.</summary>
        private const int EmuPerPixel = 9525;

        /// <summary>Padding around the image inside its cell, in pixels.</summary>
        private const int PhotoPadding = 3;

        /// <summary>Exports to .xlsx or .csv based on the file extension.</summary>
        public static void Export(string path, IList<TagRecord> records, Dictionary<Guid, byte[]> photos)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            if (records == null) records = new List<TagRecord>();
            if (photos == null) photos = new Dictionary<Guid, byte[]>();

            string extension = Path.GetExtension(path);

            if (string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
                ExportCsv(path, records);
            else
                ExportXlsx(path, records, photos);
        }

        // ---------------------------------------------------------------- CSV

        private static void ExportCsv(string path, IList<TagRecord> records)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine(string.Join(",", Escape(new[] { HeaderTagName, HeaderFileName, HeaderComments })));

            foreach (TagRecord record in records)
            {
                sb.AppendLine(string.Join(",", Escape(new[]
                {
                    record.TagName ?? string.Empty,
                    record.FileName ?? string.Empty,
                    string.Empty
                })));
            }

            // UTF-8 with BOM so Excel picks up the encoding for non-ASCII names.
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string[] Escape(string[] values)
        {
            string[] result = new string[values.Length];

            for (int i = 0; i < values.Length; i++)
            {
                string v = values[i] ?? string.Empty;
                bool needsQuotes = v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;

                if (needsQuotes) v = "\"" + v.Replace("\"", "\"\"") + "\"";

                result[i] = v;
            }

            return result;
        }

        // --------------------------------------------------------------- XLSX

        /// <summary>One row's worth of layout information.</summary>
        private sealed class RowPlan
        {
            public TagRecord Record;
            public byte[] Photo;
            public int PhotoWidth;
            public int PhotoHeight;
            public int ImageIndex;      // 1-based, matches media/imageN.png
            public int RowIndex;        // 1-based worksheet row
        }

        private static void ExportXlsx(string path, IList<TagRecord> records, Dictionary<Guid, byte[]> photos)
        {
            List<RowPlan> plans = BuildPlans(records, photos);

            if (File.Exists(path)) File.Delete(path);

            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                bool hasImages = plans.Exists(p => p.Photo != null);

                WriteText(archive, "[Content_Types].xml", ContentTypes(hasImages));
                WriteText(archive, "_rels/.rels", RootRelationships());
                WriteText(archive, "xl/workbook.xml", Workbook());
                WriteText(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships());
                WriteText(archive, "xl/styles.xml", Styles());
                WriteText(archive, "xl/worksheets/sheet1.xml", Sheet(plans, hasImages));

                if (hasImages)
                {
                    WriteText(archive, "xl/worksheets/_rels/sheet1.xml.rels", SheetRelationships());
                    WriteText(archive, "xl/drawings/drawing1.xml", Drawing(plans));
                    WriteText(archive, "xl/drawings/_rels/drawing1.xml.rels", DrawingRelationships(plans));

                    foreach (RowPlan plan in plans)
                    {
                        if (plan.Photo == null) continue;
                        WriteBytes(archive, "xl/media/image" + plan.ImageIndex.ToString(CultureInfo.InvariantCulture) + ".png", plan.Photo);
                    }
                }
            }
        }

        private static List<RowPlan> BuildPlans(IList<TagRecord> records, Dictionary<Guid, byte[]> photos)
        {
            List<RowPlan> plans = new List<RowPlan>();
            int imageIndex = 0;

            for (int i = 0; i < records.Count; i++)
            {
                TagRecord record = records[i];

                byte[] photo;
                if (!photos.TryGetValue(record.ViewpointGuid, out photo)) photo = null;

                RowPlan plan = new RowPlan
                {
                    Record = record,
                    Photo = photo,
                    RowIndex = i + 2      // row 1 is the header
                };

                if (photo != null)
                {
                    plan.ImageIndex = ++imageIndex;

                    int width, height;
                    if (TryReadPngSize(photo, out width, out height))
                    {
                        plan.PhotoWidth = width;
                        plan.PhotoHeight = height;
                    }
                    else
                    {
                        // Fall back to the nominal capture size.
                        plan.PhotoWidth = ViewpointPhotoService.PhotoWidth;
                        plan.PhotoHeight = (int)Math.Round(ViewpointPhotoService.PhotoWidth * 9.0 / 16.0);
                    }
                }

                plans.Add(plan);
            }

            return plans;
        }

        /// <summary>
        /// Reads width and height straight out of the PNG IHDR chunk, so the row
        /// can be sized without decoding the image.
        /// </summary>
        private static bool TryReadPngSize(byte[] png, out int width, out int height)
        {
            width = 0;
            height = 0;

            // 8-byte signature, 4-byte length, "IHDR", then width and height.
            if (png == null || png.Length < 24) return false;
            if (png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47) return false;

            width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];

            return width > 0 && height > 0;
        }

        private static void WriteText(ZipArchive archive, string entryName, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

            using (Stream entryStream = entry.Open())
            using (StreamWriter writer = new StreamWriter(entryStream, new UTF8Encoding(false)))
            {
                writer.Write(content);
            }
        }

        private static void WriteBytes(ZipArchive archive, string entryName, byte[] content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);

            using (Stream entryStream = entry.Open())
            {
                entryStream.Write(content, 0, content.Length);
            }
        }

        // ------------------------------------------------------- package parts

        private static string ContentTypes(bool hasImages)
        {
            StringBuilder sb = new StringBuilder();

            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");

            if (hasImages)
                sb.Append("<Default Extension=\"png\" ContentType=\"image/png\"/>");

            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            sb.Append("<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");

            if (hasImages)
                sb.Append("<Override PartName=\"/xl/drawings/drawing1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawing+xml\"/>");

            sb.Append("</Types>");

            return sb.ToString();
        }

        private static string RootRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                 + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                 + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
                 + "</Relationships>";
        }

        private static string Workbook()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                 + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
                 + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                 + "<sheets><sheet name=\"Tags\" sheetId=\"1\" r:id=\"rId1\"/></sheets>"
                 + "</workbook>";
        }

        private static string WorkbookRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                 + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                 + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                 + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"
                 + "</Relationships>";
        }

        /// <summary>
        /// Style 0 = normal, 1 = bold header, 2 = top-aligned wrapped text.
        /// </summary>
        private static string Styles()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                 + "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                 + "<fonts count=\"2\">"
                 + "<font><sz val=\"11\"/><name val=\"Calibri\"/></font>"
                 + "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>"
                 + "</fonts>"
                 + "<fills count=\"2\">"
                 + "<fill><patternFill patternType=\"none\"/></fill>"
                 + "<fill><patternFill patternType=\"gray125\"/></fill>"
                 + "</fills>"
                 + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
                 + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
                 + "<cellXfs count=\"3\">"
                 + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
                 + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>"
                 + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\">"
                 + "<alignment vertical=\"top\" wrapText=\"1\"/></xf>"
                 + "</cellXfs>"
                 + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>"
                 + "</styleSheet>";
        }

        private static string SheetRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                 + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                 + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"../drawings/drawing1.xml\"/>"
                 + "</Relationships>";
        }

        private static string Sheet(List<RowPlan> plans, bool hasImages)
        {
            StringBuilder sb = new StringBuilder();

            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
            sb.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");

            // Column widths are in "characters"; roughly pixels / 7.
            int photoColumnWidth = (ViewpointPhotoService.PhotoWidth + PhotoPadding * 2) / 7 + 1;

            sb.Append("<cols>");
            sb.Append("<col min=\"1\" max=\"1\" width=\"14\" customWidth=\"1\"/>");
            sb.Append("<col min=\"2\" max=\"2\" width=\"42\" customWidth=\"1\"/>");
            sb.Append("<col min=\"3\" max=\"3\" width=\"").Append(photoColumnWidth.ToString(CultureInfo.InvariantCulture)).Append("\" customWidth=\"1\"/>");
            sb.Append("<col min=\"4\" max=\"4\" width=\"46\" customWidth=\"1\"/>");
            sb.Append("</cols>");

            sb.Append("<sheetData>");

            // Header
            sb.Append("<row r=\"1\" ht=\"20\" customHeight=\"1\">");
            AppendCell(sb, "A1", HeaderTagName, 1);
            AppendCell(sb, "B1", HeaderFileName, 1);
            AppendCell(sb, "C1", HeaderPhoto, 1);
            AppendCell(sb, "D1", HeaderComments, 1);
            sb.Append("</row>");

            foreach (RowPlan plan in plans)
            {
                string row = plan.RowIndex.ToString(CultureInfo.InvariantCulture);

                // Row height in points = pixels * 0.75.
                double heightPoints = plan.Photo != null
                    ? (plan.PhotoHeight + PhotoPadding * 2) * 0.75
                    : 18.0;

                sb.Append("<row r=\"").Append(row).Append("\" ht=\"");
                sb.Append(heightPoints.ToString("0.##", CultureInfo.InvariantCulture));
                sb.Append("\" customHeight=\"1\">");

                AppendCell(sb, "A" + row, plan.Record.TagName ?? string.Empty, 2);
                AppendCell(sb, "B" + row, plan.Record.FileName ?? string.Empty, 2);

                // The photo cell is left empty - the image floats over it from
                // the drawing part. Note it when there is no photo.
                AppendCell(sb, "C" + row, plan.Photo == null ? "(no photo)" : string.Empty, 2);
                AppendCell(sb, "D" + row, string.Empty, 2);

                sb.Append("</row>");
            }

            sb.Append("</sheetData>");

            // The drawing reference must come after sheetData.
            if (hasImages) sb.Append("<drawing r:id=\"rId1\"/>");

            sb.Append("</worksheet>");

            return sb.ToString();
        }

        private static void AppendCell(StringBuilder sb, string reference, string value, int styleIndex)
        {
            sb.Append("<c r=\"").Append(reference).Append("\" s=\"");
            sb.Append(styleIndex.ToString(CultureInfo.InvariantCulture));
            sb.Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">");
            sb.Append(XmlEscape(value));
            sb.Append("</t></is></c>");
        }

        private static string Drawing(List<RowPlan> plans)
        {
            StringBuilder sb = new StringBuilder();

            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" ");
            sb.Append("xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" ");
            sb.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");

            foreach (RowPlan plan in plans)
            {
                if (plan.Photo == null) continue;

                long cx = (long)plan.PhotoWidth * EmuPerPixel;
                long cy = (long)plan.PhotoHeight * EmuPerPixel;
                long offset = (long)PhotoPadding * EmuPerPixel;

                // Column 2 is "C" (zero based); row index is zero based too.
                int zeroBasedRow = plan.RowIndex - 1;
                int shapeId = plan.ImageIndex + 1;

                sb.Append("<xdr:oneCellAnchor>");
                sb.Append("<xdr:from>");
                sb.Append("<xdr:col>2</xdr:col>");
                sb.Append("<xdr:colOff>").Append(offset.ToString(CultureInfo.InvariantCulture)).Append("</xdr:colOff>");
                sb.Append("<xdr:row>").Append(zeroBasedRow.ToString(CultureInfo.InvariantCulture)).Append("</xdr:row>");
                sb.Append("<xdr:rowOff>").Append(offset.ToString(CultureInfo.InvariantCulture)).Append("</xdr:rowOff>");
                sb.Append("</xdr:from>");

                sb.Append("<xdr:ext cx=\"").Append(cx.ToString(CultureInfo.InvariantCulture));
                sb.Append("\" cy=\"").Append(cy.ToString(CultureInfo.InvariantCulture)).Append("\"/>");

                sb.Append("<xdr:pic>");
                sb.Append("<xdr:nvPicPr>");
                sb.Append("<xdr:cNvPr id=\"").Append(shapeId.ToString(CultureInfo.InvariantCulture));
                sb.Append("\" name=\"").Append(XmlEscape(plan.Record.TagName ?? "Photo")).Append("\"/>");
                sb.Append("<xdr:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></xdr:cNvPicPr>");
                sb.Append("</xdr:nvPicPr>");

                sb.Append("<xdr:blipFill>");
                sb.Append("<a:blip r:embed=\"rId").Append(plan.ImageIndex.ToString(CultureInfo.InvariantCulture)).Append("\"/>");
                sb.Append("<a:stretch><a:fillRect/></a:stretch>");
                sb.Append("</xdr:blipFill>");

                sb.Append("<xdr:spPr>");
                sb.Append("<a:xfrm><a:off x=\"0\" y=\"0\"/>");
                sb.Append("<a:ext cx=\"").Append(cx.ToString(CultureInfo.InvariantCulture));
                sb.Append("\" cy=\"").Append(cy.ToString(CultureInfo.InvariantCulture)).Append("\"/></a:xfrm>");
                sb.Append("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>");
                sb.Append("</xdr:spPr>");

                sb.Append("</xdr:pic>");
                sb.Append("<xdr:clientData/>");
                sb.Append("</xdr:oneCellAnchor>");
            }

            sb.Append("</xdr:wsDr>");

            return sb.ToString();
        }

        private static string DrawingRelationships(List<RowPlan> plans)
        {
            StringBuilder sb = new StringBuilder();

            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");

            foreach (RowPlan plan in plans)
            {
                if (plan.Photo == null) continue;

                string index = plan.ImageIndex.ToString(CultureInfo.InvariantCulture);

                sb.Append("<Relationship Id=\"rId").Append(index);
                sb.Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\"");
                sb.Append(" Target=\"../media/image").Append(index).Append(".png\"/>");
            }

            sb.Append("</Relationships>");

            return sb.ToString();
        }

        private static string XmlEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            StringBuilder sb = new StringBuilder(value.Length + 16);

            foreach (char c in value)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default:
                        // Strip control characters that are illegal in XML 1.0.
                        if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') break;
                        sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }
    }
}
