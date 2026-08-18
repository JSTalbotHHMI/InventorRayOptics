using System;
using System.IO;
using System.Linq;
using System.Text;

namespace InventorRayOptics
{
    /// <summary>
    /// A personal library of named materials, shared across every Inventor document —
    /// one flat JSON file per material under %APPDATA%\InventorRayOptics\Materials. Each
    /// file's contents are opaque to this class: whatever JSON the web panel's material
    /// object serializes to (IOR coefficients, phosphor config, etc.) is stored verbatim
    /// and handed back verbatim, so this side never needs to understand that shape.
    /// </summary>
    internal static class MaterialLibrary
    {
        private static string FolderPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "InventorRayOptics", "Materials");

        private static string SanitizedPath(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (var c in name) sb.Append(invalid.Contains(c) ? '_' : c);
            return Path.Combine(FolderPath, sb + ".json");
        }

        public static string[] ListNames()
        {
            if (!Directory.Exists(FolderPath)) return new string[0];
            return Directory.GetFiles(FolderPath, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static void Save(string name, string materialJson)
        {
            Directory.CreateDirectory(FolderPath);
            File.WriteAllText(SanitizedPath(name), materialJson);
        }

        public static void Delete(string name)
        {
            var path = SanitizedPath(name);
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>Raw JSON object literal mapping name -> material, ready to embed
        /// directly into a message to the web panel.</summary>
        public static string LoadAllAsJsonObject()
        {
            if (!Directory.Exists(FolderPath)) return "{}";
            var sb = new StringBuilder("{");
            var files = Directory.GetFiles(FolderPath, "*.json");
            for (int i = 0; i < files.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var name = Path.GetFileNameWithoutExtension(files[i]);
                sb.Append(JsonHelper.Serialize(name)).Append(':').Append(File.ReadAllText(files[i]));
            }
            sb.Append('}');
            return sb.ToString();
        }
    }
}
