using System;
using System.IO;
using System.Linq;
using System.Text;

namespace InventorRayOptics
{
    /// <summary>
    /// Named ray-optics settings snapshots (per-body materials, per-face reflectivity/
    /// dichroic/phosphor overrides, light sources, tracing params — see app.js's
    /// serializeSettings/applySettings) stored as individual files next to the active
    /// Inventor document, so they travel with it on disk and multiple named snapshots
    /// can coexist for the same document.
    /// </summary>
    internal static class SettingsStore
    {
        public static string FolderFor(string documentFullFileName)
        {
            var dir = Path.GetDirectoryName(documentFullFileName);
            var baseName = Path.GetFileNameWithoutExtension(documentFullFileName);
            return Path.Combine(dir ?? ".", baseName + ".rayoptics");
        }

        private static string SanitizedPath(string documentFullFileName, string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (var c in name) sb.Append(invalid.Contains(c) ? '_' : c);
            return Path.Combine(FolderFor(documentFullFileName), sb + ".json");
        }

        public static string[] ListNames(string documentFullFileName)
        {
            var folder = FolderFor(documentFullFileName);
            if (!Directory.Exists(folder)) return new string[0];
            return Directory.GetFiles(folder, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static void Save(string documentFullFileName, string name, string json)
        {
            Directory.CreateDirectory(FolderFor(documentFullFileName));
            File.WriteAllText(SanitizedPath(documentFullFileName, name), json);
        }

        public static string Load(string documentFullFileName, string name)
        {
            var path = SanitizedPath(documentFullFileName, name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }
}
