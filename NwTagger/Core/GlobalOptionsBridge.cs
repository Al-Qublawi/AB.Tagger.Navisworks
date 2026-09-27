using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using Microsoft.Win32;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Interop;

namespace NwTagger.Core
{
    /// <summary>
    /// Reads and writes Navisworks GlobalOptions.
    ///
    /// The .NET API deliberately does not expose the global option tree
    /// (Application.Options only surfaces Grids), so we go through the same
    /// place Navisworks itself persists them - HKCU - and then ask the kernel
    /// to reload, which is the supported apply path:
    ///
    ///   HKCU\Software\Autodesk\Navisworks Manage\{ver}\GlobalOptions\interface\redline
    ///       font_size       = "2 16"      (2 = Int32)
    ///       small_font_size = "2 14"
    ///       line_width      = "2 3"
    ///       font_name       = "4 Tahoma"  (4 = DisplayString)
    ///   ...\interface\redline\color
    ///       red/green/blue  = "1 0.5"     (1 = Double)
    ///   ...\interface\smart_tags
    ///       hide_category   = "3 1"       (3 = Boolean)
    ///
    /// A value of plain "0" means "unset, use the built-in default".
    /// </summary>
    internal static class GlobalOptionsBridge
    {
        private const int TypeDouble = 1;
        private const int TypeInt32 = 2;
        private const int TypeBoolean = 3;
        private const int TypeDisplayString = 4;

        private const string RedlinePath = @"\interface\redline";
        private const string RedlineColorPath = @"\interface\redline\color";
        private const string SmartTagsPath = @"\interface\smart_tags";

        private static string _cachedRoot;
        private static bool _rootProbed;

        /// <summary>
        /// Locates "...\GlobalOptions" for the running Navisworks, or null.
        /// Prefers the version matching the loaded API, then falls back to
        /// whichever key actually contains interface\redline.
        /// </summary>
        internal static string GlobalOptionsRoot()
        {
            if (_rootProbed) return _cachedRoot;
            _rootProbed = true;

            try
            {
                string preferredVersion = null;
                try
                {
                    preferredVersion = Application.Version.ApiMajor.ToString(CultureInfo.InvariantCulture) + ".0";
                }
                catch
                {
                    preferredVersion = null;
                }

                using (RegistryKey autodesk = Registry.CurrentUser.OpenSubKey(@"Software\Autodesk"))
                {
                    if (autodesk == null) return null;

                    List<string> candidates = new List<string>();

                    foreach (string productName in autodesk.GetSubKeyNames())
                    {
                        if (productName.IndexOf("Navisworks", StringComparison.OrdinalIgnoreCase) < 0) continue;

                        using (RegistryKey product = autodesk.OpenSubKey(productName))
                        {
                            if (product == null) continue;

                            foreach (string version in product.GetSubKeyNames())
                            {
                                string path = @"Software\Autodesk\" + productName + @"\" + version + @"\GlobalOptions";

                                using (RegistryKey probe = Registry.CurrentUser.OpenSubKey(path + @"\interface\redline"))
                                {
                                    if (probe == null) continue;
                                }

                                if (preferredVersion != null &&
                                    string.Equals(version, preferredVersion, StringComparison.OrdinalIgnoreCase))
                                {
                                    _cachedRoot = path;
                                    return _cachedRoot;
                                }

                                candidates.Add(path);
                            }
                        }
                    }

                    if (candidates.Count > 0)
                    {
                        candidates.Sort(StringComparer.OrdinalIgnoreCase);
                        _cachedRoot = candidates[candidates.Count - 1];
                    }
                }
            }
            catch
            {
                _cachedRoot = null;
            }

            return _cachedRoot;
        }

        private static string EncodeInt(int v)
        {
            return TypeInt32.ToString(CultureInfo.InvariantCulture) + " " +
                   v.ToString(CultureInfo.InvariantCulture);
        }

        private static string EncodeBool(bool v)
        {
            return TypeBoolean.ToString(CultureInfo.InvariantCulture) + " " + (v ? "1" : "0");
        }

        private static string EncodeString(string v)
        {
            return TypeDisplayString.ToString(CultureInfo.InvariantCulture) + " " + (v ?? string.Empty);
        }

        private static string EncodeDouble(double v)
        {
            // Navisworks writes these with 19 decimal places.
            return TypeDouble.ToString(CultureInfo.InvariantCulture) + " " +
                   v.ToString("0.0000000000000000000", CultureInfo.InvariantCulture);
        }

        private static bool WriteValue(string subPath, string name, string encoded)
        {
            string root = GlobalOptionsRoot();
            if (root == null) return false;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(root + subPath))
                {
                    if (key == null) return false;
                    key.SetValue(name, encoded, RegistryValueKind.String);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string ReadValue(string subPath, string name)
        {
            string root = GlobalOptionsRoot();
            if (root == null) return null;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(root + subPath))
                {
                    if (key == null) return null;
                    object v = key.GetValue(name);
                    return v == null ? null : v.ToString();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Pushes the tagger text settings into the Navisworks redline defaults and
        /// reloads the kernel so they take effect on the next redline drawn.
        ///
        /// Font name and size are global to the document - that is a Navisworks
        /// constraint, not ours. Colour is also set per redline object so that
        /// individual tags keep their own colour regardless of this.
        /// </summary>
        internal static bool ApplyTextSettings(TaggerSettings settings, out string message)
        {
            message = null;

            string root = GlobalOptionsRoot();
            if (root == null)
            {
                message = "Could not locate Navisworks GlobalOptions in the registry. " +
                          "Tags will use the current Navisworks redline font.";
                return false;
            }

            bool ok = true;
            ok &= WriteValue(RedlinePath, "font_size", EncodeInt(settings.TextSize));
            ok &= WriteValue(RedlinePath, "small_font_size", EncodeInt(Math.Max(6, settings.TextSize - 2)));
            ok &= WriteValue(RedlinePath, "font_name", EncodeString(settings.FontName));
            ok &= WriteValue(RedlinePath, "line_width", EncodeInt(settings.LeaderWidth));

            System.Drawing.Color c = settings.TextColor;
            ok &= WriteValue(RedlineColorPath, "red", EncodeDouble(c.R / 255.0));
            ok &= WriteValue(RedlineColorPath, "green", EncodeDouble(c.G / 255.0));
            ok &= WriteValue(RedlineColorPath, "blue", EncodeDouble(c.B / 255.0));

            // "Add category title" is the Navisworks quick-property switch, inverted.
            ok &= WriteValue(SmartTagsPath, "hide_category", EncodeBool(!settings.AddCategoryTitle));
            ok &= WriteValue(SmartTagsPath, "enabled", EncodeBool(true));

            if (!ok)
            {
                message = "Some redline options could not be written to the registry.";
                return false;
            }

            // Ask the kernel to pick the new values up. This is what Navisworks
            // does itself when you press OK in the Options dialog.
            try
            {
                LcOpRegistry.LoadGlobalOptions();
            }
            catch (Exception ex)
            {
                message = "Settings saved, but Navisworks did not reload them (" + ex.Message +
                          "). They will apply after a restart.";
                return false;
            }

            // Verify: ask the kernel to write its in-memory options back out, then
            // read them. If Navisworks rejected a value it will have overwritten it.
            try
            {
                LcOpRegistry.SaveGlobalOptions();

                string back = ReadValue(RedlinePath, "font_size");
                string expected = EncodeInt(settings.TextSize);
                string actual = back == null ? string.Empty : back.Trim();

                // A bare "0" is not a size - it is how Navisworks records "not
                // set, use the built-in default", and it is exactly what it
                // writes back when the size it was handed IS that default (14,
                // out of the box). Treating that as a rejection put a dialog in
                // front of anyone who left Size alone, every single time they
                // pressed ENABLE, about nothing at all. A different concrete
                // value really is a rejection, and is still reported.
                bool usingBuiltInDefault = actual.Length == 0 || actual == "0";

                if (!usingBuiltInDefault &&
                    !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    message = "Navisworks did not accept the text size (it reports \"" + back +
                              "\"). Tags will still be created, using the current redline font.";
                    return false;
                }
            }
            catch
            {
                // Non-fatal: the apply above already happened.
            }

            return true;
        }

        /// <summary>Reads the current Navisworks redline point size, or null if unset.</summary>
        internal static int? ReadRedlineFontSize()
        {
            string raw = ReadValue(RedlinePath, "font_size");
            if (string.IsNullOrEmpty(raw)) return null;

            string[] parts = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return null;

            int value;
            if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return value;

            return null;
        }
    }
}
