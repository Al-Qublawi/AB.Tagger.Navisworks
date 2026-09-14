using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.ComApi;
using Autodesk.Navisworks.Api.Interop.ComApi;

namespace NwTagger.Core
{
    /// <summary>
    /// Produces the tag text for an element from the Navisworks Quick Properties
    /// only - never the full property dump.
    ///
    /// Quick Properties are called "smart tags" internally. The COM state object
    /// exposes SmartTagText(path), which returns exactly the text Navisworks
    /// itself would show in the quick-property tooltip, honouring the definitions
    /// the user configured under Options > Interface > Quick Properties and the
    /// hide_category switch. That is far more faithful than re-implementing the
    /// lookup, so we use it as the primary source.
    /// </summary>
    public static class QuickPropertyReader
    {
        /// <summary>
        /// Builds the finished, ordered set of text lines for one element.
        /// Never returns null, and never returns an empty list.
        /// </summary>
        public static List<string> BuildTagLines(ModelItem item, TaggerSettings settings)
        {
            List<string> lines = new List<string>();
            if (item == null) return lines;

            string displayName = SafeDisplayName(item);

            if (settings.IncludeItemName)
                lines.Add("Item Name: " + displayName);

            // Primary source: the Navisworks quick-property text itself.
            string quick = TryReadSmartTagText(item);
            List<string> quickLines = SplitLines(quick);

            // If quick properties already lead with the item name, do not repeat it.
            if (settings.IncludeItemName && quickLines.Count > 0 && LooksLikeSameName(quickLines[0], displayName))
                quickLines.RemoveAt(0);

            lines.AddRange(quickLines);

            // Fallback: no quick properties are configured for this element.
            if (lines.Count == (settings.IncludeItemName ? 1 : 0))
                lines.AddRange(FallbackProperties(item, settings));

            if (lines.Count == 0)
                lines.Add("Item Name: " + displayName);

            return lines;
        }

        /// <summary>
        /// Asks Navisworks for the quick-property text of an item.
        /// Returns null when the COM bridge is unavailable or nothing is defined.
        /// </summary>
        private static string TryReadSmartTagText(ModelItem item)
        {
            try
            {
                InwOpState10 state = ComApiBridge.State;
                if (state == null) return null;

                InwOaPath path = ComApiBridge.ToInwOaPath(item);
                if (path == null) return null;

                string text = state.SmartTagText(path);
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch
            {
                // COM bridge not available in this context - fall through.
                return null;
            }
        }

        /// <summary>
        /// Used only when the model has no quick-property definitions. Emits the
        /// handful of fields Navisworks shows by default rather than every property.
        /// </summary>
        private static IEnumerable<string> FallbackProperties(ModelItem item, TaggerSettings settings)
        {
            List<string> result = new List<string>();

            try
            {
                PropertyCategoryCollection categories = item.GetUserFilteredPropertyCategories();
                if (categories == null) return result;

                foreach (PropertyCategory category in categories)
                {
                    // "Item" is the built-in category holding Type / Name / Source File.
                    if (!string.Equals(category.DisplayName, "Item", StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (DataProperty property in category.Properties)
                    {
                        string name = property.DisplayName;
                        if (string.IsNullOrEmpty(name)) continue;

                        // Name is already emitted as the "Item Name:" header.
                        if (settings.IncludeItemName &&
                            string.Equals(name, "Name", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string value = VariantToString(property.Value);
                        if (string.IsNullOrEmpty(value)) continue;

                        result.Add(settings.AddCategoryTitle ? name + ": " + value : value);
                    }

                    break;
                }
            }
            catch
            {
                // Leave whatever was collected.
            }

            return result;
        }

        /// <summary>Converts any Navisworks property value into display text.</summary>
        public static string VariantToString(VariantData value)
        {
            if (value == null) return string.Empty;

            try
            {
                switch (value.DataType)
                {
                    case VariantDataType.DisplayString:
                        return value.ToDisplayString();
                    case VariantDataType.IdentifierString:
                        return value.ToIdentifierString();
                    case VariantDataType.Boolean:
                        return value.ToBoolean() ? "Yes" : "No";
                    case VariantDataType.Int32:
                        return value.ToInt32().ToString(CultureInfo.CurrentCulture);
                    case VariantDataType.Double:
                    case VariantDataType.DoubleLength:
                    case VariantDataType.DoubleArea:
                    case VariantDataType.DoubleVolume:
                    case VariantDataType.DoubleAngle:
                        return value.ToAnyDouble().ToString("0.###", CultureInfo.CurrentCulture);
                    case VariantDataType.DateTime:
                        return value.ToDateTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
                    case VariantDataType.NamedConstant:
                        NamedConstant nc = value.ToNamedConstant();
                        return nc == null ? string.Empty : nc.DisplayName;
                    case VariantDataType.None:
                        return string.Empty;
                    default:
                        return WideIntegerToString(value);
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Formats the wide integer types - Int64, Nat32, Nat64.
        ///
        /// Those three arrived after Navisworks 2025, so naming them directly
        /// stops this file compiling against the 2025 API. Resolving them by
        /// name keeps one source file for every supported release: on 2025 the
        /// lookups simply come back null and these values cannot occur anyway.
        /// They matter because a Revit Element ID can arrive as one of them.
        /// </summary>
        private static string WideIntegerToString(VariantData value)
        {
            string typeName = value.DataType.ToString();

            MethodInfo getter;
            if (WideIntegerGetters.TryGetValue(typeName, out getter) && getter != null)
            {
                try
                {
                    object result = getter.Invoke(value, null);
                    if (result != null) return Convert.ToString(result, CultureInfo.CurrentCulture);
                }
                catch
                {
                    // fall through to the generic rendering
                }
            }

            return value.ToString();
        }

        private static readonly Dictionary<string, MethodInfo> WideIntegerGetters = BuildWideIntegerGetters();

        private static Dictionary<string, MethodInfo> BuildWideIntegerGetters()
        {
            Dictionary<string, MethodInfo> map = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);

            foreach (string name in new[] { "Int64", "Nat32", "Nat64" })
            {
                try { map[name] = typeof(VariantData).GetMethod("To" + name, Type.EmptyTypes); }
                catch { map[name] = null; }
            }

            return map;
        }

        private static string SafeDisplayName(ModelItem item)
        {
            try
            {
                if (!string.IsNullOrEmpty(item.DisplayName)) return item.DisplayName;
                if (!string.IsNullOrEmpty(item.ClassDisplayName)) return item.ClassDisplayName;
            }
            catch
            {
                // fall through
            }
            return "(unnamed)";
        }

        private static List<string> SplitLines(string text)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;

            string[] raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string line in raw)
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0) result.Add(trimmed);
            }

            return result;
        }

        private static bool LooksLikeSameName(string quickLine, string displayName)
        {
            if (string.IsNullOrEmpty(quickLine) || string.IsNullOrEmpty(displayName)) return false;

            int colon = quickLine.IndexOf(':');
            string value = colon >= 0 ? quickLine.Substring(colon + 1).Trim() : quickLine.Trim();

            return string.Equals(value, displayName.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
