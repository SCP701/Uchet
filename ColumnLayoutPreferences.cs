using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace WorkLedger
{
    internal static class ColumnLayoutPreferences
    {
        private sealed class SavedLayout
        {
            public SavedLayout() { }
            public int Version { get; set; }
            public Dictionary<string, Dictionary<string, int>> Widths { get; set; }
        }
        internal static string PathFor(string dataDir, string scope)
        {
            string profile = UiPreferences.GetProfilePath(dataDir), suffix;
            using (var hash = SHA256.Create()) suffix = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(scope))).Replace("-", "").ToLowerInvariant();
            string user = profile.Substring(profile.LastIndexOf(Path.DirectorySeparatorChar) + 1, 16);
            return Path.Combine(Path.GetFullPath(dataDir), "ui-preferences", "columns-" + user + "-" + suffix.Substring(0, 16) + ".json");
        }
        internal static Dictionary<string, Dictionary<string, int>> Load(string path)
        {
            var empty = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 262144) return empty;
                var saved = new JavaScriptSerializer { MaxJsonLength = 262144, RecursionLimit = 8 }.Deserialize<SavedLayout>(File.ReadAllText(path, Encoding.UTF8));
                if (saved == null || saved.Version != 1 || saved.Widths == null || saved.Widths.Count > 128) return empty;
                foreach (var context in saved.Widths)
                {
                    if (context.Key.Length > 256 || context.Value == null || context.Value.Count > 256) return empty;
                    foreach (var width in context.Value)
                        if (width.Key.Length > 512 || width.Value < 2 || width.Value > 65536) return empty;
                }
                return saved.Widths;
            }
            catch (IOException) { return empty; }
            catch (UnauthorizedAccessException) { return empty; }
            catch (ArgumentException) { return empty; }
            catch (InvalidOperationException) { return empty; }
            catch (FormatException) { return empty; }
            catch (OverflowException) { return empty; }
        }
        internal static void Save(string path, Dictionary<string, Dictionary<string, int>> widths)
        {
            if (String.IsNullOrEmpty(path)) return;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                string json = new JavaScriptSerializer { MaxJsonLength = 262144 }.Serialize(new SavedLayout { Version = 1, Widths = widths });
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            // A read-only preferences folder must not interrupt editing records.
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
