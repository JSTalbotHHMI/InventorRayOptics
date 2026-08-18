using Inventor;
// Inventor's type library defines its own Path/File types (Inventor.Path, Inventor.File)
// that collide with System.IO; alias System.IO's version explicitly everywhere.
using IOPath = System.IO.Path;
using IODirectory = System.IO.Directory;

namespace InventorRayOptics
{
    /// <summary>
    /// Exports the active Inventor document to a temporary STEP file so the web app can
    /// read the true B-rep geometry via OpenCascade.
    /// </summary>
    public static class StepExporter
    {
        // Inventor's STEP translator add-in GUID. Stable across Inventor versions.
        private const string StepTranslatorId = "{90AF7F40-0C01-11D5-8E83-0010B541CD80}";

        public static string ExportActive(Inventor.Application inv, Document doc)
        {
            var outDir = IOPath.Combine(IOPath.GetTempPath(), "InventorRayOptics");
            IODirectory.CreateDirectory(outDir);
            var outPath = IOPath.Combine(outDir, "model.step");

            var addIn = (ApplicationAddIn)inv.ApplicationAddIns.ItemById[StepTranslatorId];
            if (!addIn.Activated)
            {
                addIn.Activate();
            }
            var translator = (TranslatorAddIn)addIn;

            var context = inv.TransientObjects.CreateTranslationContext();
            context.Type = IOMechanismEnum.kFileBrowseIOMechanism;

            var options = inv.TransientObjects.CreateNameValueMap();
            if (translator.HasSaveCopyAsOptions[doc, context, options])
            {
                // ApplicationProtocolType: 3 = AP214 (widely supported). Some Inventor
                // versions also accept AP242; AP214 is the safe default for OCCT reading.
                options.Value["ApplicationProtocolType"] = 3;
            }

            var media = inv.TransientObjects.CreateDataMedium();
            media.FileName = outPath;

            translator.SaveCopyAs(doc, context, options, media);

            return outPath;
        }

        /// <summary>Solid-body names as Inventor itself shows them (e.g. "Solid1", or
        /// whatever the user renamed it to), in the same order OpenCascade's
        /// TopExp_Explorer will walk the exported STEP file's TopAbs_SOLID entities —
        /// index-matched, not read back out of the STEP file itself (Inventor's STEP
        /// translator doesn't reliably preserve per-body names there). Returns null for
        /// anything other than a part document — an assembly's bodies live inside each
        /// occurrence's underlying part with no single flat list to index against the
        /// STEP export's combined shape.</summary>
        public static string[] TryGetBodyNames(Document doc)
        {
            var partDoc = doc as PartDocument;
            if (partDoc == null) return null;

            var bodies = partDoc.ComponentDefinition.SurfaceBodies;
            var names = new string[bodies.Count];
            for (int i = 1; i <= bodies.Count; i++) names[i - 1] = bodies[i].Name;
            return names;
        }
    }
}
