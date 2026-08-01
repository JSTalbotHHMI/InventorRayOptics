using System.Runtime.InteropServices;
using Inventor;
using IOPath = System.IO.Path;

namespace InventorRayOptics
{
    /// <summary>
    /// Inventor add-in entry point. Adds a "Ray Optics" entry to the Environments tab
    /// (alongside Stress Analysis / Inventor Studio) that switches into a dedicated
    /// "Ray Optics" environment and opens a dockable panel showing the optical trace
    /// for the active document.
    /// </summary>
    [Guid("8210C5FB-411B-4F93-9034-58FEFBFA35BC"), ComVisible(true)]
    [ProgId("InventorRayOptics.StandardAddInServer")]
    public class StandardAddInServer : ApplicationAddInServer
    {
        public const string AddInClientId = "{8210C5FB-411B-4F93-9034-58FEFBFA35BC}";
        private const string TabInternalName = "IROptics:Tab";
        private const string EnvironmentInternalName = "IROptics:Environment";

        private Inventor.Application _inv;
        private ButtonDefinition _launchBtn;
        private ButtonDefinition _refreshBtn;
        private OpticsDockable _dockable;

        public void Activate(ApplicationAddInSite addInSiteObject, bool firstTime)
        {
            _inv = addInSiteObject.Application;

            var resourcesDir = IOPath.Combine(
                IOPath.GetDirectoryName(GetType().Assembly.Location) ?? ".", "Resources");
            object smallIcon = null, largeIcon = null;
            try
            {
                smallIcon = IconHelper.LoadPictureDisp(IOPath.Combine(resourcesDir, "icon16.png"));
                largeIcon = IconHelper.LoadPictureDisp(IOPath.Combine(resourcesDir, "icon32.png"));
            }
            catch
            {
                // fall back to Inventor's default placeholder icon rather than failing to load
            }

            var defs = _inv.CommandManager.ControlDefinitions;
            _launchBtn = defs.AddButtonDefinition(
                "Ray Optics",
                "IROptics:Launch",
                CommandTypesEnum.kNonShapeEditCmdType,
                AddInClientId,
                "Trace light rays through the active part or assembly",
                "Open the optical ray-tracing environment",
                smallIcon,
                largeIcon);
            _launchBtn.OnExecute += OnLaunch;

            _refreshBtn = defs.AddButtonDefinition(
                "Refresh Model",
                "IROptics:Refresh",
                CommandTypesEnum.kNonShapeEditCmdType,
                AddInClientId,
                "Re-export the active document and reload it in the Ray Optics panel",
                "Sync the panel with the current model");
            _refreshBtn.OnExecute += OnLaunch; // re-export + reload is exactly what launching does

            SetUpEnvironment("Part", smallIcon, largeIcon);
            SetUpEnvironment("Assembly", smallIcon, largeIcon);
        }

        private void SetUpEnvironment(string ribbonName, object smallIcon, object largeIcon)
        {
            Ribbon ribbon;
            try { ribbon = _inv.UserInterfaceManager.Ribbons[ribbonName]; }
            catch { return; } // ribbon not present in this Inventor configuration

            // Entry point lives on the Environments tab, next to Stress Analysis / Studio.
            RibbonTab environmentsTab = ribbon.RibbonTabs["id_TabEnvironments"];
            RibbonPanel entryPanel;
            try { entryPanel = environmentsTab.RibbonPanels["IROptics:EntryPanel"]; }
            catch { entryPanel = environmentsTab.RibbonPanels.Add("Ray Optics", "IROptics:EntryPanel", AddInClientId); }
            entryPanel.CommandControls.AddButton(_launchBtn, true);

            // Dedicated contextual tab that becomes active while the environment is entered.
            // Contextual=true keeps it hidden outside our environment; Inventor auto-provides
            // the "Finish"/return affordance for custom environments (Environment.ExitDisplayName).
            RibbonTab ourTab;
            try { ourTab = ribbon.RibbonTabs[TabInternalName]; }
            catch
            {
                ourTab = ribbon.RibbonTabs.Add(
                    "Ray Optics", TabInternalName, AddInClientId,
                    "id_TabEnvironments", false, true);
            }
            RibbonPanel ourPanel;
            try { ourPanel = ourTab.RibbonPanels["IROptics:TabPanel"]; }
            catch { ourPanel = ourTab.RibbonPanels.Add("Ray Optics", "IROptics:TabPanel", AddInClientId); }
            ourPanel.CommandControls.AddButton(_refreshBtn, true);

            Environments environments = _inv.UserInterfaceManager.Environments;
            Environment env;
            try { env = environments[EnvironmentInternalName]; }
            catch
            {
                env = environments.Add(
                    "Ray Optics", EnvironmentInternalName, AddInClientId, smallIcon, largeIcon);
            }
            env.DefaultRibbonTab = TabInternalName;
            env.AdditionalVisibleRibbonTabs = new[] { TabInternalName };
            env.InheritAllRibbonTabs = true; // keep modeling tabs available — this tool is a
                                              // passive viewer, not an exclusive editing mode
        }

        private void OnLaunch(NameValueMap context)
        {
            Document doc = _inv.ActiveDocument;
            if (doc == null) return;

            try
            {
                // EnvironmentManager lives on PartDocument/AssemblyDocument, not the shared
                // Document interface — late-bind so either document type works here.
                dynamic dynDoc = doc;
                Environments environments = _inv.UserInterfaceManager.Environments;
                Environment env = environments[EnvironmentInternalName];
                dynDoc.EnvironmentManager.SetCurrentEnvironment(env, AddInClientId);
            }
            catch
            {
                // non-fatal: still show the panel even if the environment switch failed
                // (e.g. this document type has no Environments tab)
            }

            if (_dockable == null)
            {
                _dockable = new OpticsDockable(_inv);
            }
            _dockable.ShowFor(doc);
        }

        public void Deactivate()
        {
            _dockable?.Dispose();
            _dockable = null;

            if (_launchBtn != null)
            {
                _launchBtn.OnExecute -= OnLaunch;
                _launchBtn = null;
            }
            if (_refreshBtn != null)
            {
                _refreshBtn.OnExecute -= OnLaunch;
                _refreshBtn = null;
            }

            if (_inv != null)
            {
                Marshal.ReleaseComObject(_inv);
                _inv = null;
            }
        }

        public void ExecuteCommand(int commandID)
        {
            // Legacy entry point; unused (buttons are wired via OnExecute).
        }

        public object Automation => null;
    }
}
