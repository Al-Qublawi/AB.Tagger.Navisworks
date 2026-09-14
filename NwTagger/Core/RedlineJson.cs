using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NwTagger.Core
{
    /// <summary>
    /// Reads and writes the Navisworks 2027 redline wire format.
    ///
    /// Captured from a live 2027 session via View.GetRedlines(). The whole
    /// schema, verbatim:
    ///
    ///   {"Type":"RedlineCollection","Version":1,"Values":[
    ///     {"Type":"RedlineText","Version":1,"Color":[1,0,0],
    ///      "Origin":[-0.160091817148,0.013983923787],"Text":"ABC123"},
    ///     {"Type":"RedlineArrow","Version":1,"Thickness":1,"Color":[1,0,0],
    ///      "Start":[-0.182273213499,0.016877149398],
    ///      "End":[-0.292215786722,-0.034236503064]}
    ///   ]}
    ///
    /// Points are camera space - the same space LcOpRedline.ScreenToCameraSpace
    /// returns - so the coordinates need no conversion beyond what the 2026 path
    /// already does. Colour components are 0..1. Text carries no size: that is
    /// the global interface.redline.font_size option, exactly as in 2026.
    ///
    /// Hand-written rather than using a JSON library, because the plugin has to
    /// stay dependency-free to load inside Navisworks.
    /// </summary>
    public static class RedlineJson
    {
        private const string CollectionHeader = "{\"Type\":\"RedlineCollection\",\"Version\":1,\"Values\":[";

        /// <summary>An empty collection - what Navisworks reports for a clean view.</summary>
        public static string Empty
        {
            get { return "{\"Type\":\"RedlineCollection\",\"Version\":1,\"Values\":[]}"; }
        }

        // ------------------------------------------------------------- write

        /// <summary>Serialises markup into the string View.SetRedlines expects.</summary>
        public static string Serialize(IEnumerable<MarkupItem> items)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append(CollectionHeader);

            bool first = true;

            if (items != null)
            {
                foreach (MarkupItem item in items)
                {
                    if (item == null) continue;

                    if (!first) sb.Append(',');
                    first = false;

                    MarkupText text = item as MarkupText;
                    if (text != null) { AppendText(sb, text); continue; }

                    MarkupArrow arrow = item as MarkupArrow;
                    if (arrow != null) { AppendArrow(sb, arrow); continue; }

                    throw new NotSupportedException("Unknown markup type: " + item.GetType().Name);
                }
            }

            sb.Append("]}");

            return sb.ToString();
        }

        private static void AppendText(StringBuilder sb, MarkupText text)
        {
            sb.Append("{\"Type\":\"RedlineText\",\"Version\":1,\"Color\":");
            AppendColor(sb, text);
            sb.Append(",\"Origin\":");
            AppendPoint(sb, text.OriginX, text.OriginY);
            sb.Append(",\"Text\":");
            AppendString(sb, text.Text);
            sb.Append('}');
        }

        private static void AppendArrow(StringBuilder sb, MarkupArrow arrow)
        {
            sb.Append("{\"Type\":\"RedlineArrow\",\"Version\":1,\"Thickness\":");
            sb.Append(arrow.Thickness.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"Color\":");
            AppendColor(sb, arrow);
            sb.Append(",\"Start\":");
            AppendPoint(sb, arrow.StartX, arrow.StartY);
            sb.Append(",\"End\":");
            AppendPoint(sb, arrow.EndX, arrow.EndY);
            sb.Append('}');
        }

        private static void AppendColor(StringBuilder sb, MarkupItem item)
        {
            sb.Append('[');
            AppendNumber(sb, item.ColorR);
            sb.Append(',');
            AppendNumber(sb, item.ColorG);
            sb.Append(',');
            AppendNumber(sb, item.ColorB);
            sb.Append(']');
        }

        private static void AppendPoint(StringBuilder sb, double x, double y)
        {
            sb.Append('[');
            AppendNumber(sb, x);
            sb.Append(',');
            AppendNumber(sb, y);
            sb.Append(']');
        }

        /// <summary>
        /// Round-trip ("R") formatting, matching what Navisworks emits: whole
        /// numbers appear bare (1, 0) and fractions keep full precision.
        /// </summary>
        private static void AppendNumber(StringBuilder sb, double value)
        {
            sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void AppendString(StringBuilder sb, string value)
        {
            sb.Append('"');

            if (!string.IsNullOrEmpty(value))
            {
                foreach (char c in value)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20)
                                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else
                                sb.Append(c);
                            break;
                    }
                }
            }

            sb.Append('"');
        }

        // -------------------------------------------------------------- read

        /// <summary>
        /// Parses a redline collection. Returns an empty list for an empty or
        /// unreadable document rather than throwing, since a malformed string
        /// from the viewer should not take the tagger down.
        /// </summary>
        public static List<MarkupItem> Parse(string json)
        {
            List<MarkupItem> items = new List<MarkupItem>();
            if (string.IsNullOrEmpty(json)) return items;

            try
            {
                // Start inside "Values":[ ... ]. Without this the first match is
                // the enclosing RedlineCollection object itself, whose closing
                // brace is the end of the document - swallowing every entry.
                int index = 0;
                int values = json.IndexOf("\"Values\":", StringComparison.Ordinal);
                if (values >= 0)
                {
                    int bracket = json.IndexOf('[', values);
                    if (bracket >= 0) index = bracket + 1;
                }

                while (true)
                {
                    int objectStart = json.IndexOf("{\"Type\":\"Redline", index, StringComparison.Ordinal);
                    if (objectStart < 0) break;

                    int typeStart = objectStart + "{\"Type\":\"".Length;
                    int typeEnd = json.IndexOf('"', typeStart);
                    if (typeEnd < 0) break;

                    string type = json.Substring(typeStart, typeEnd - typeStart);
                    int objectEnd = FindObjectEnd(json, objectStart);
                    if (objectEnd < 0) break;

                    string body = json.Substring(objectStart, objectEnd - objectStart + 1);

                    if (type == "RedlineCollection") { index = objectStart + 1; continue; }

                    if (type == "RedlineText") AddText(items, body);
                    else if (type == "RedlineArrow") AddArrow(items, body);
                    // Other redline kinds (cloud, ellipse, freehand) are read by
                    // Navisworks but never written by the tagger; skipping them
                    // here would drop them, so callers must not round-trip
                    // through Parse when preserving foreign markup matters.

                    index = objectEnd + 1;
                }
            }
            catch
            {
                // Return whatever parsed cleanly.
            }

            return items;
        }

        /// <summary>Walks to the matching brace, ignoring braces inside strings.</summary>
        private static int FindObjectEnd(string json, int start)
        {
            int depth = 0;
            bool inString = false;
            bool escaped = false;

            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];

                if (inString)
                {
                    if (escaped) { escaped = false; continue; }
                    if (c == '\\') { escaped = true; continue; }
                    if (c == '"') inString = false;
                    continue;
                }

                if (c == '"') { inString = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }

            return -1;
        }

        private static void AddText(List<MarkupItem> items, string body)
        {
            double[] colour = ReadArray(body, "\"Color\":", 3);
            double[] origin = ReadArray(body, "\"Origin\":", 2);
            string text = ReadString(body, "\"Text\":");

            if (colour == null || origin == null) return;

            items.Add(new MarkupText
            {
                ColorR = colour[0],
                ColorG = colour[1],
                ColorB = colour[2],
                OriginX = origin[0],
                OriginY = origin[1],
                Text = text
            });
        }

        private static void AddArrow(List<MarkupItem> items, string body)
        {
            double[] colour = ReadArray(body, "\"Color\":", 3);
            double[] start = ReadArray(body, "\"Start\":", 2);
            double[] end = ReadArray(body, "\"End\":", 2);

            if (colour == null || start == null || end == null) return;

            items.Add(new MarkupArrow
            {
                ColorR = colour[0],
                ColorG = colour[1],
                ColorB = colour[2],
                StartX = start[0],
                StartY = start[1],
                EndX = end[0],
                EndY = end[1],
                Thickness = (int)Math.Round(ReadNumber(body, "\"Thickness\":", 1))
            });
        }

        private static double[] ReadArray(string body, string key, int count)
        {
            int at = body.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return null;

            int open = body.IndexOf('[', at);
            int close = body.IndexOf(']', open + 1);
            if (open < 0 || close < 0) return null;

            string[] parts = body.Substring(open + 1, close - open - 1).Split(',');
            if (parts.Length < count) return null;

            double[] values = new double[count];

            for (int i = 0; i < count; i++)
            {
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                    return null;
            }

            return values;
        }

        private static double ReadNumber(string body, string key, double fallback)
        {
            int at = body.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return fallback;

            int start = at + key.Length;
            int end = start;

            while (end < body.Length && (char.IsDigit(body[end]) || body[end] == '.' || body[end] == '-' ||
                                         body[end] == '+' || body[end] == 'e' || body[end] == 'E'))
                end++;

            double value;
            if (double.TryParse(body.Substring(start, end - start), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out value))
                return value;

            return fallback;
        }

        private static string ReadString(string body, string key)
        {
            int at = body.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return string.Empty;

            int open = body.IndexOf('"', at + key.Length);
            if (open < 0) return string.Empty;

            StringBuilder sb = new StringBuilder();
            bool escaped = false;

            for (int i = open + 1; i < body.Length; i++)
            {
                char c = body[i];

                if (escaped)
                {
                    switch (c)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 < body.Length)
                            {
                                int code;
                                if (int.TryParse(body.Substring(i + 1, 4), NumberStyles.HexNumber,
                                                 CultureInfo.InvariantCulture, out code))
                                {
                                    sb.Append((char)code);
                                    i += 4;
                                }
                            }
                            break;
                        default: sb.Append(c); break;
                    }

                    escaped = false;
                    continue;
                }

                if (c == '\\') { escaped = true; continue; }
                if (c == '"') break;

                sb.Append(c);
            }

            return sb.ToString();
        }
    }
}
