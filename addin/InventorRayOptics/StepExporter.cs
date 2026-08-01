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
    }
}
