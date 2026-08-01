using System.Runtime.InteropServices;
using Inventor;

namespace InventorRayOptics
{
    /// <summary>
    /// Inventor add-in entry point. Adds a "Ray Optics" button to the Part and Assembly
    /// ribbons that opens a dockable panel showing the optical trace for the active document.
    /// </summary>
    [Guid("8210C5FB-411B-4F93-9034-58FEFBFA35BC"), ComVisible(true)]
    [ProgId("InventorRayOptics.StandardAddInServer")]
    public class StandardAddInServer : ApplicationAddInServer
    {
        public const string AddInClientId = "{8210C5FB-411B-4F93-9034-58FEFBFA35BC}";

        private Inventor.Application _inv;
        private ButtonDefinition _launchBtn;
        private OpticsDockable _dockable;

        public void Activate(ApplicationAddInSite addInSiteObject, bool firstTime)
        {
            _inv = addInSiteObject.Application;

            var defs = _inv.CommandManager.ControlDefinitions;
            _launchBtn = defs.AddButtonDefinition(
                "Ray Optics",
                "IROptics:Launch",
                CommandTypesEnum.kNonShapeEditCmdType,
                AddInClientId,
                "Trace light rays through the active part or assembly",
                "Open the optical ray-tracing panel");
            _launchBtn.OnExecute += OnLaunch;

            AddButtonToRibbon("Part");
            AddButtonToRibbon("Assembly");
        }

        private void AddButtonToRibbon(string ribbonName)
        {
            Ribbon ribbon;
            try { ribbon = _inv.UserInterfaceManager.Ribbons[ribbonName]; }
            catch { return; } // ribbon not present in this Inventor configuration

            RibbonTab tab = ribbon.RibbonTabs["id_TabTools"];

            RibbonPanel panel;
            try { panel = tab.RibbonPanels["IROptics:Panel"]; }
            catch { panel = tab.RibbonPanels.Add("Ray Optics", "IROptics:Panel", AddInClientId); }

            panel.CommandControls.AddButton(_launchBtn, true);
        }

        private void OnLaunch(NameValueMap context)
        {
            var doc = _inv.ActiveDocument;
            if (doc == null) return;

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
