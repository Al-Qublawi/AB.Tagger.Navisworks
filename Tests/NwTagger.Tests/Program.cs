using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NwTagger.Core;

namespace NwTagger.Tests
{
    /// <summary>
    /// Everything about the tagger that can be checked with Navisworks closed.
    ///
    /// The export is the reason this exists: a sheet that repeated the same
    /// viewpoint photo once per tag is not something a compiler catches, and it
    /// should not need a Navisworks licence to catch either. The redline JSON and
    /// the text layout ride along, since they are plain code too.
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static int Main(string[] args)
        {
            bool quiet = args != null && args.Contains("-q");

            Console.WriteLine("AB Tagger tests");
            Console.WriteLine(new string('=', 52));

            Run("export: one row per viewpoint", ExportGroupsByViewpoint);
            Run("export: the row is named for the viewpoint", ExportNamesTheViewpointRange);
            Run("export: each source file listed once", ExportListsEachFileOnce);
            Run("export: one photo per viewpoint, embedded once", ExportEmbedsOnePhotoPerViewpoint);
            Run("export: header plus one row each", ExportWritesHeaderAndOneRowEach);
            Run("export: tags with no viewpoint stay apart", ExportKeepsUnidentifiedTagsApart);
            Run("export: a missing photo is noted in its cell", ExportNotesMissingPhoto);
            Run("export: rows are sized to the photo", ExportSizesRowsToThePhoto);
            Run("export: csv groups the same way", CsvGroupsTheSameWay);
            Run("export: every workbook part is valid xml", WorkbookPartsAreValidXml);
            Run("export: a tag name with & or < survives", ExportEscapesXml);

            Run("redline json: round trips", RedlineJsonRoundTrips);
            Run("redline json: matches the captured 2027 sample", RedlineJsonMatchesSample);
            Run("redline json: escapes quotes and backslashes", RedlineJsonEscapesText);
            Run("redline json: an empty set is an empty collection", RedlineJsonEmpty);

            Run("text layout: long lines are wrapped", TextWrapSplitsLongLines);
            Run("text layout: wrapping off leaves lines alone", TextWrapOffLeavesLinesAlone);
            Run("text layout: the leader leaves the edge facing the element", LeaderLeavesFacingEdge);

            Console.WriteLine();
            Console.WriteLine(new string('=', 52));
            Console.WriteLine("{0} passed, {1} failed", _passed, _failed);

            if (!quiet && Environment.UserInteractive && _failed > 0)
                Console.WriteLine("(run with -q to skip this note)");

            return _failed == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------- harness

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine("  PASS  " + name);
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("  FAIL  " + name);
                Console.WriteLine("        " + ex.Message);
            }
        }

        private static void That(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void Equal(object expected, object actual, string what)
        {
            if (!Equals(expected, actual))
                throw new Exception(what + ": expected <" + expected + ">, got <" + actual + ">");
        }

        // ------------------------------------------------------- test data

        private static readonly Guid ViewpointOne = new Guid("11111111-1111-1111-1111-111111111111");
        private static readonly Guid ViewpointTwo = new Guid("22222222-2222-2222-2222-222222222222");

        private static TagRecord Tag(string tagName, string viewpointName, Guid viewpoint, string file)
        {
            return new TagRecord
            {
                CreatedUtc = DateTime.UtcNow,
                TagName = tagName,
                ViewpointName = viewpointName,
                ViewpointGuid = viewpoint,
                FileName = file,
                Lines = new List<string> { "Item Name: Wall", "Source File: " + file },
                ViewAspect = 16.0 / 9.0,
                SourceViewHeight = 900,
                TextSize = 14,
                LeaderWidth = 2
            };
        }

        /// <summary>Three tags in one viewpoint and two in another - the case that used to repeat photos.</summary>
        private static List<TagRecord> FiveTagsInTwoViewpoints()
        {
            return new List<TagRecord>
            {
                Tag("Tag 01", "Tag 01", ViewpointOne, "arch.rvt"),
                Tag("Tag 02", "Tag 01-02", ViewpointOne, "arch.rvt"),
                Tag("Tag 03", "Tag 01-03", ViewpointOne, "mep.rvt"),
                Tag("Tag 04", "Tag 04", ViewpointTwo, "struct.rvt"),
                Tag("Tag 05", "Tag 04-05", ViewpointTwo, "struct.rvt")
            };
        }

        private static Dictionary<Guid, byte[]> TwoPhotos()
        {
            return new Dictionary<Guid, byte[]>
            {
                { ViewpointOne, Png(520, 292) },
                { ViewpointTwo, Png(520, 292) }
            };
        }

        /// <summary>A real PNG, so the exporter reads its size out of the IHDR chunk.</summary>
        private static byte[] Png(int width, int height)
        {
            using (Bitmap bitmap = new Bitmap(width, height))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (MemoryStream stream = new MemoryStream())
            {
                graphics.Clear(Color.LightGray);
                bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }

        private static string TempFile(string extension)
        {
            string path = Path.Combine(Path.GetTempPath(),
                "abtagger-tests-" + Guid.NewGuid().ToString("N") + extension);
            return path;
        }

        // ------------------------------------------------------ the workbook

        /// <summary>One entry of a written workbook, as text.</summary>
        private static string Part(string path, string entryName)
        {
            using (FileStream stream = File.OpenRead(path))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                ZipArchiveEntry entry = archive.GetEntry(entryName);
                That(entry != null, "the workbook has no " + entryName);

                using (StreamReader reader = new StreamReader(entry.Open()))
                    return reader.ReadToEnd();
            }
        }

        private static List<string> Entries(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
                return archive.Entries.Select(e => e.FullName).ToList();
        }

        /// <summary>The text of every cell in one column, in row order.</summary>
        private static List<string> Column(string sheetXml, string column)
        {
            List<string> values = new List<string>();

            foreach (Match match in Regex.Matches(sheetXml,
                "<c r=\"" + column + "(\\d+)\"[^>]*>(.*?)</c>", RegexOptions.Singleline))
            {
                Match text = Regex.Match(match.Groups[2].Value, "<t[^>]*>(.*?)</t>", RegexOptions.Singleline);
                values.Add(text.Success ? text.Groups[1].Value : string.Empty);
            }

            return values;
        }

        // ------------------------------------------------------------ export

        private static void ExportGroupsByViewpoint()
        {
            string path = TempFile(".xlsx");

            try
            {
                int rows = TagExporter.Export(path, FiveTagsInTwoViewpoints(), TwoPhotos());
                Equal(2, rows, "rows written for five tags in two viewpoints");
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void ExportNamesTheViewpointRange()
        {
            string path = TempFile(".xlsx");

            try
            {
                TagExporter.Export(path, FiveTagsInTwoViewpoints(), TwoPhotos());

                List<string> names = Column(Part(path, "xl/worksheets/sheet1.xml"), "A");

                Equal(3, names.Count, "cells in column A (header plus two rows)");
                Equal("Tag Name", names[0], "header");
                Equal("Tag 01-03", names[1], "first viewpoint's row");
                Equal("Tag 04-05", names[2], "second viewpoint's row");
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void ExportListsEachFileOnce()
        {
            string path = TempFile(".xlsx");

            try
            {
                TagExporter.Export(path, FiveTagsInTwoViewpoints(), TwoPhotos());

                List<string> files = Column(Part(path, "xl/worksheets/sheet1.xml"), "B");

                Equal("arch.rvt, mep.rvt", files[1], "files of the first viewpoint");
                Equal("struct.rvt", files[2], "files of the second viewpoint");
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void ExportEmbedsOnePhotoPerViewpoint()
        {
            string path = TempFile(".xlsx");

            try
            {
                TagExporter.Export(path, FiveTagsInTwoViewpoints(), TwoPhotos());

                List<string> entries = Entries(path);
                int images = entries.Count(e => e.StartsWith("xl/media/", StringComparison.OrdinalIgnoreCase));

                Equal(2, images, "images in the package for two viewpoints");

                string drawing = Part(path, "xl/drawings/drawing1.xml");
                Equal(2, Regex.Matches(drawing, "<xdr:oneCellAnchor>").Count, "pictures placed on the sheet");
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void ExportWritesHeaderAndOneRowEach()
        {
            string path = TempFile(".xlsx");

            try
            {
                TagExporter.Export(path, FiveTagsInTwoViewpoints(), TwoPhotos());

                string sheet = Part(path, "xl/worksheets/sheet1.xml");
                Equal(3, Regex.Matches(sheet, "<row ").Count, "rows in the worksheet");
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void ExportKeepsUnidentifiedTagsApart()
        {
            string path = TempFile(".xlsx");

            try
            {
                List<TagRecord> records = new List<TagRecord>
                {
                    Tag("Tag 01", "Tag 01", Guid.Empty, "arch.rvt"),
                    Tag("Tag 02", "Tag 02", Guid.Empty, "arch.rvt")
                };

                int rows = TagExporter.Export(path, records, null);

                Equal(2, rows, "two tags with no viewpoint give two rows");
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void ExportNotesMissingPhoto()
        {
            string path = TempFile(".xlsx");

            try
            {
                TagExporter.Export(path, FiveTagsInTwoViewpoints(), null);

                List<string> photoCells = Column(Part(path, "xl/worksheets/sheet1.xml"), "C");

                Equal("(no photo)", photoCells[1], "photo cell of a row without a photo");
                That(!Entries(path).Any(e => e.StartsWith("xl/media/", StringComparison.OrdinalIgnoreCase)),
                     "a workbook with no photos should carry no images");
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void ExportSizesRowsToThePhoto()
        {
            string path = TempFile(".xlsx");

            try
            {
                Dictionary<Guid, byte[]> photos = new Dictionary<Guid, byte[]>
                {
                    { ViewpointOne, Png(520, 292) },
                    { ViewpointTwo, Png(520, 160) }
                };

                TagExporter.Export(path, FiveTagsInTwoViewpoints(), photos);

                string sheet = Part(path, "xl/worksheets/sheet1.xml");
                MatchCollection heights = Regex.Matches(sheet, "<row r=\"(\\d+)\" ht=\"([0-9.]+)\"");

                Equal(3, heights.Count, "rows with an explicit height");

                double tall = double.Parse(heights[1].Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                double shortRow = double.Parse(heights[2].Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);

                That(tall > shortRow, "the row with the taller photo should be taller: " + tall + " vs " + shortRow);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void CsvGroupsTheSameWay()
        {
            string path = TempFile(".csv");

            try
            {
                int rows = TagExporter.Export(path, FiveTagsInTwoViewpoints(), null);
                Equal(2, rows, "csv rows for five tags in two viewpoints");

                string[] lines = File.ReadAllLines(path);
                Equal(3, lines.Length, "lines in the csv (header plus two rows)");
                That(lines[1].StartsWith("Tag 01-03"), "first csv row should name the viewpoint: " + lines[1]);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void WorkbookPartsAreValidXml()
        {
            string path = TempFile(".xlsx");

            try
            {
                TagExporter.Export(path, FiveTagsInTwoViewpoints(), TwoPhotos());

                foreach (string entry in Entries(path))
                {
                    if (!entry.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                        !entry.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;

                    string xml = Part(path, entry);

                    try { XDocument.Parse(xml); }
                    catch (Exception ex) { throw new Exception(entry + " is not valid xml: " + ex.Message); }
                }

                That(Entries(path).Contains("[Content_Types].xml"), "the package needs [Content_Types].xml");
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void ExportEscapesXml()
        {
            string path = TempFile(".xlsx");

            try
            {
                List<TagRecord> records = new List<TagRecord>
                {
                    Tag("Tag 01", "Tag 01", ViewpointOne, "R&D <draft>.rvt")
                };

                TagExporter.Export(path, records, null);

                string sheet = Part(path, "xl/worksheets/sheet1.xml");
                XDocument.Parse(sheet);

                That(sheet.Contains("R&amp;D &lt;draft&gt;.rvt"), "the file name should be xml escaped");
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ----------------------------------------------------- redline json

        private static void RedlineJsonRoundTrips()
        {
            List<MarkupItem> items = new List<MarkupItem>
            {
                new MarkupArrow { StartX = -0.18227321349986, StartY = 0.016877149398135, EndX = -0.29221578672200, EndY = -0.034236503064789, Thickness = 3, ColorR = 1, ColorG = 0, ColorB = 0 },
                new MarkupText { OriginX = -0.16009181714803, OriginY = 0.013983923787026, Text = "Item Name: Wall", ColorR = 1, ColorG = 0.5, ColorB = 0 }
            };

            string json = RedlineJson.Serialize(items);
            List<MarkupItem> parsed = RedlineJson.Parse(json);

            Equal(2, parsed.Count, "items parsed back");

            MarkupArrow arrow = parsed[0] as MarkupArrow;
            That(arrow != null, "the first item should be an arrow");
            Equal(3, arrow.Thickness, "arrow thickness");
            That(Math.Abs(arrow.EndY - (-0.034236503064789)) < 1e-12, "arrow end y");

            MarkupText text = parsed[1] as MarkupText;
            That(text != null, "the second item should be text");
            Equal("Item Name: Wall", text.Text, "text");
            That(Math.Abs(text.ColorG - 0.5) < 1e-12, "text green component");

            // And serialising what was parsed gives the same string back.
            Equal(json, RedlineJson.Serialize(parsed), "re-serialised json");
        }

        /// <summary>
        /// The format as Navisworks 2027 itself emitted it, captured from a live
        /// session. Parsing and re-writing it has to reproduce it exactly, or what
        /// we write is not what Navisworks reads.
        /// </summary>
        private static void RedlineJsonMatchesSample()
        {
            const string sample =
                "{\"Type\":\"RedlineCollection\",\"Version\":1,\"Values\":[" +
                "{\"Type\":\"RedlineText\",\"Version\":1,\"Color\":[1,0,0],\"Origin\":[-0.16009181714802512,0.013983923787026247],\"Text\":\"ABC123\"}," +
                "{\"Type\":\"RedlineArrow\",\"Version\":1,\"Thickness\":1,\"Color\":[1,0,0],\"Start\":[-0.18227321349985995,0.016877149398135144],\"End\":[-0.29221578672199766,-0.03423650306478853]}" +
                "]}";

            List<MarkupItem> parsed = RedlineJson.Parse(sample);
            Equal(2, parsed.Count, "items in the captured sample");

            Equal(sample, RedlineJson.Serialize(parsed), "the sample re-written byte for byte");
        }

        private static void RedlineJsonEscapesText()
        {
            // Tag text comes from model properties, which really do contain these.
            string awkward = "Pipe \"600\\ø\" \t line";

            List<MarkupItem> items = new List<MarkupItem>
            {
                new MarkupText { OriginX = 0, OriginY = 0, Text = awkward, ColorR = 1, ColorG = 1, ColorB = 1 }
            };

            string json = RedlineJson.Serialize(items);
            List<MarkupItem> parsed = RedlineJson.Parse(json);

            MarkupText text = parsed[0] as MarkupText;
            That(text != null, "the item should come back as text");
            Equal(awkward, text.Text, "awkward text round trip");
        }

        private static void RedlineJsonEmpty()
        {
            Equal(RedlineJson.Empty, RedlineJson.Serialize(new List<MarkupItem>()), "an empty markup set");
            Equal(0, RedlineJson.Parse(RedlineJson.Empty).Count, "items in an empty collection");
        }

        // ------------------------------------------------------ text layout

        private static void TextWrapSplitsLongLines()
        {
            List<string> wrapped = TextLayout.Wrap(
                new List<string> { "Componente Nombre: LADRILLO PASTELERO 24x24 sobre losa aligerada" },
                true, 20);

            That(wrapped.Count > 1, "a long line should be split, got " + wrapped.Count + " line(s)");

            foreach (string line in wrapped)
                That(line.Length <= 24, "no line should run far past the limit: \"" + line + "\"");
        }

        private static void TextWrapOffLeavesLinesAlone()
        {
            string line = "Componente Nombre: LADRILLO PASTELERO 24x24 sobre losa aligerada";

            List<string> wrapped = TextLayout.Wrap(new List<string> { line }, false, 20);

            Equal(1, wrapped.Count, "lines when wrapping is off");
            Equal(line, wrapped[0], "the line itself");
        }

        /// <summary>
        /// The leader has to leave the side of the text block that faces the
        /// element, so it never cuts back across its own text.
        /// </summary>
        private static void LeaderLeavesFacingEdge()
        {
            int blockWidth = 200;
            int lines = 3;
            int lineHeight = 18;

            int textX = 400;
            int textY = 300;

            int attachX, attachY;

            // Element down and to the right: the leader leaves the bottom right.
            TextLayout.LeaderAttachPoint(textX, textY, blockWidth, lines, lineHeight, 900, 700, out attachX, out attachY);
            That(attachX >= textX + blockWidth - 2, "should leave the right edge, got x=" + attachX);
            That(attachY >= textY, "should leave below the top, got y=" + attachY);

            // Element to the left: the leader leaves the left edge.
            TextLayout.LeaderAttachPoint(textX, textY, blockWidth, lines, lineHeight, 50, 320, out attachX, out attachY);
            That(attachX <= textX + 2, "should leave the left edge, got x=" + attachX);
        }
    }
}
