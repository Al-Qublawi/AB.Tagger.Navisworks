using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Navisworks.Api;

namespace NwTagger.Core
{
    /// <summary>
    /// Works out a usable identifier for a Navisworks element.
    ///
    /// Navisworks has no native integer element id the way Revit does, so we
    /// look for the "Element ID" property that the Revit / IFC exporters write,
    /// and fall back to the model-tree index path, which is always available
    /// and unique within the document.
    /// </summary>
    public static class ElementIdResolver
    {
        private static readonly string[] IdPropertyNames =
        {
            "Element ID",
            "ElementId",
            "Element Id",
            "Id",
            "GUID",
            "IfcGUID"
        };

        private static readonly string[] IdCategoryNames =
        {
            "Element ID",
            "Revit Element ID",
            "Entity Handle",
            "Item"
        };

        /// <summary>
        /// Returns the best available identifier. Never null or empty.
        /// </summary>
        public static string Resolve(ModelItem item)
        {
            if (item == null) return "unknown";

            string fromProperty = TryReadIdProperty(item);
            if (!string.IsNullOrEmpty(fromProperty)) return fromProperty;

            string fromPath = TryBuildIndexPath(item);
            if (!string.IsNullOrEmpty(fromPath)) return fromPath;

            try
            {
                return "H" + item.InstanceHashCode.ToString("X", CultureInfo.InvariantCulture);
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>
        /// Scans the element properties for something that looks like an authored id.
        /// Prefers a dedicated "Element ID" category, then any matching property name.
        /// </summary>
        private static string TryReadIdProperty(ModelItem item)
        {
            try
            {
                PropertyCategoryCollection categories = item.PropertyCategories;
                if (categories == null) return null;

                // Pass 1: categories that exist specifically to carry the id.
                foreach (PropertyCategory category in categories)
                {
                    if (!MatchesAny(category.DisplayName, IdCategoryNames)) continue;

                    foreach (DataProperty property in category.Properties)
                    {
                        if (!MatchesAny(property.DisplayName, IdPropertyNames)) continue;

                        string value = QuickPropertyReader.VariantToString(property.Value);
                        if (!string.IsNullOrEmpty(value)) return Sanitise(value);
                    }
                }

                // Pass 2: any category at all.
                foreach (PropertyCategory category in categories)
                {
                    foreach (DataProperty property in category.Properties)
                    {
                        if (!MatchesAny(property.DisplayName, IdPropertyNames)) continue;

                        string value = QuickPropertyReader.VariantToString(property.Value);
                        if (!string.IsNullOrEmpty(value)) return Sanitise(value);
                    }
                }
            }
            catch
            {
                // fall through to the path-based id
            }

            return null;
        }

        /// <summary>
        /// Builds a model-tree index path such as "2:14:3" by walking up to the root
        /// and recording each item position among its siblings.
        /// </summary>
        private static string TryBuildIndexPath(ModelItem item)
        {
            try
            {
                List<int> indices = new List<int>();
                ModelItem current = item;

                // Guard against unexpectedly deep or cyclic trees.
                for (int depth = 0; depth < 64 && current != null; depth++)
                {
                    ModelItem parent = current.Parent;
                    if (parent == null) break;

                    int index = 0;
                    int found = -1;

                    foreach (ModelItem sibling in parent.Children)
                    {
                        if (sibling != null && sibling.Equals(current))
                        {
                            found = index;
                            break;
                        }
                        index++;
                    }

                    if (found < 0) return null;

                    indices.Add(found);
                    current = parent;
                }

                if (indices.Count == 0) return null;

                indices.Reverse();

                string[] parts = new string[indices.Count];
                for (int i = 0; i < indices.Count; i++)
                    parts[i] = indices[i].ToString(CultureInfo.InvariantCulture);

                return string.Join(":", parts);
            }
            catch
            {
                return null;
            }
        }

        private static bool MatchesAny(string candidate, string[] names)
        {
            if (string.IsNullOrEmpty(candidate)) return false;

            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(candidate, names[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>Strips characters that are not legal in a viewpoint name.</summary>
        private static string Sanitise(string value)
        {
            string trimmed = value.Trim();
            if (trimmed.Length > 64) trimmed = trimmed.Substring(0, 64);

            char[] chars = trimmed.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (char.IsControl(c) || c == '\\' || c == '/') chars[i] = '_';
            }

            return new string(chars);
        }
    }
}
