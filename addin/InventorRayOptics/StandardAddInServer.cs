using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Inventor;
using IOPath = System.IO.Path;
using SysException = System.Exception;

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
        private ButtonDefinition _closeBtn;
        private ButtonDefinition _newMaterialBtn;
        private ButtonDefinition _deleteMaterialBtn;
        private ButtonDefinition _saveSettingsBtn;
        private ButtonDefinition _loadSettingsBtn;
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
                "Trace light rays through the active part or assembly (build " + BuildInfo.Version + ")",
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

            _closeBtn = defs.AddButtonDefinition(
                "Close Ray Optics",
                "IROptics:Close",
                CommandTypesEnum.kNonShapeEditCmdType,
                AddInClientId,
                "Close the ray-tracing panel and return to normal modeling",
                "Close the Ray Optics environment");
            _closeBtn.OnExecute += OnClose;

            _newMaterialBtn = defs.AddButtonDefinition(
                "Save Material to Library",
                "IROptics:MaterialNew",
                CommandTypesEnum.kNonShapeEditCmdType,
                AddInClientId,
                "Save a body's current material as a named entry in your personal material library",
                "Add the current material to your library");
            _newMaterialBtn.OnExecute += OnNewMaterial;

            _deleteMaterialBtn = defs.AddButtonDefinition(
                "Delete Library Material",
                "IROptics:MaterialDelete",
                CommandTypesEnum.kNonShapeEditCmdType,
                AddInClientId,
                "Remove a saved entry from your personal material library",
                "Delete a material from your library");
            _deleteMaterialBtn.OnExecute += OnDeleteMaterial;

            _saveSettingsBtn = defs.AddButtonDefinition(
                "Save Settings",
                "IROptics:SettingsSave",
                CommandTypesEnum.kNonShapeEditCmdType,
                AddInClientId,
                "Save the current bodies/surfaces/lights configuration under a name, alongside this document",
                "Save the current Ray Optics settings");
            _saveSettingsBtn.OnExecute += OnSaveSettings;

            _loadSettingsBtn = defs.AddButtonDefinition(
                "Load Settings",
                "IROptics:SettingsLoad",
                CommandTypesEnum.kNonShapeEditCmdType,
                AddInClientId,
                "Load a previously-saved bodies/surfaces/lights configuration for this document",
                "Load saved Ray Optics settings");
            _loadSettingsBtn.OnExecute += OnLoadSettings;

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
            ourPanel.CommandControls.AddButton(_saveSettingsBtn, true);
            ourPanel.CommandControls.AddButton(_loadSettingsBtn, true);
            ourPanel.CommandControls.AddButton(_newMaterialBtn, true);
            ourPanel.CommandControls.AddButton(_deleteMaterialBtn, true);
            ourPanel.CommandControls.AddButton(_closeBtn, true);

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
                EnvironmentManager envMgr = ((dynamic)doc).EnvironmentManager;
                Environments environments = _inv.UserInterfaceManager.Environments;
                Environment env = environments[EnvironmentInternalName];

                // "" is the correct EditObjectId for entering an environment that isn't
                // editing a specific object (this argument is an edit target, not a
                // ClientId — see OnClose). The add-in GUID that used to be passed here is
                // rejected outright by some calls; keep it only as a last-ditch fallback.
                var failures = new System.Collections.Generic.List<string>();
                TryEnvironmentCall(() => envMgr.SetCurrentEnvironment(env, ""),
                    "SetCurrentEnvironment(env, \"\")", failures);
                if (!IsInOurEnvironment(envMgr))
                {
                    TryEnvironmentCall(() => envMgr.OverrideEnvironment = env,
                        "OverrideEnvironment = env", failures);
                }
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

        // Hides the panel and destroys the browser along with all its scene state, then
        // leaves the Ray Optics environment so the contextual ribbon tab goes away. The
        // OpticsDockable instance itself is kept (see CloseEnvironment) — Inventor owns
        // the DockableWindow for the whole session, so discarding and recreating our
        // wrapper made the next Launch click fail with E_INVALIDARG.
        private void OnClose(NameValueMap context)
        {
            _dockable?.CloseEnvironment();

            Document doc = _inv.ActiveDocument;
            if (doc == null) return;

            EnvironmentManager envMgr;
            try
            {
                // EnvironmentManager lives on PartDocument/AssemblyDocument, not the
                // shared Document interface — late-bind to reach it, but keep the result
                // strongly typed so GetCurrentEnvironment's out-parameters work.
                envMgr = ((dynamic)doc).EnvironmentManager;
            }
            catch
            {
                return; // document type with no environments — nothing to leave
            }

            if (!IsInOurEnvironment(envMgr)) return; // already out

            // Inventor exposes two different ways to leave an environment and which one
            // applies isn't something the type library makes obvious: SetCurrentEnvironment's
            // second argument is an *EditObjectId* (not a ClientId — passing the add-in GUID
            // there is what produced "Value does not fall within the expected range"), while
            // OverrideEnvironment is the push/pop mechanism. Try each and verify against
            // GetCurrentEnvironment rather than assuming any of them took effect, since a
            // silent no-op here is exactly what left the contextual tab stranded before.
            var failures = new System.Collections.Generic.List<string>();

            TryEnvironmentCall(() => envMgr.SetCurrentEnvironment(envMgr.BaseEnvironment, ""),
                "SetCurrentEnvironment(base, \"\")", failures);
            if (!IsInOurEnvironment(envMgr)) return;

            TryEnvironmentCall(() => envMgr.OverrideEnvironment = null,
                "OverrideEnvironment = null", failures);
            if (!IsInOurEnvironment(envMgr)) return;

            MessageBox.Show(
                "The Ray Optics panel closed, but Inventor would not switch back to the " +
                "normal modeling environment. You can leave it manually from the " +
                "Environments tab.\n\nDetails:\n" + string.Join("\n", failures),
                "Ray Optics", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static void TryEnvironmentCall(System.Action attempt, string label,
            System.Collections.Generic.List<string> failures)
        {
            try { attempt(); }
            catch (SysException ex) { failures.Add($"  {label} -> {ex.Message}"); }
        }

        private static bool IsInOurEnvironment(EnvironmentManager envMgr)
        {
            try
            {
                Environment current;
                string editTargetId;
                envMgr.GetCurrentEnvironment(out current, out editTargetId);
                return current != null && current.InternalName == EnvironmentInternalName;
            }
            catch
            {
                return false; // can't tell; treat as "not stuck" rather than nag the user
            }
        }

        private bool RequirePanelOpen()
        {
            if (_dockable != null && _dockable.IsOpen) return true;
            MessageBox.Show(
                "Open the Ray Optics panel first (click \"Ray Optics\" on the Environments tab).",
                "Ray Optics", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        // Asks the web panel which bodies exist (with their current material), lets the
        // user pick one (skipped if there's only one) and name it, then stores that
        // body's material verbatim in the shared on-disk library (see MaterialLibrary)
        // and pushes the updated library back down so every open dropdown picks it up.
        private async void OnNewMaterial(NameValueMap context)
        {
            if (!RequirePanelOpen()) return;
            try
            {
                var bodiesJson = await _dockable.RequestAsync("listBodiesRequest");
                var bodies = JsonHelper.Deserialize(bodiesJson) as System.Collections.Generic.List<object>;
                if (bodies == null || bodies.Count == 0)
                {
                    MessageBox.Show("No bodies in the current model.", "Ray Optics",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var entries = bodies.Cast<System.Collections.Generic.Dictionary<string, object>>().ToList();
                System.Collections.Generic.Dictionary<string, object> chosen;
                if (entries.Count == 1)
                {
                    chosen = entries[0];
                }
                else
                {
                    var label = PromptDialogs.PromptForChoice(
                        "Save Material to Library", "Which body's material do you want to save?",
                        entries.Select(e => (string)e["label"]));
                    if (label == null) return;
                    chosen = entries.First(e => (string)e["label"] == label);
                }

                var name = PromptDialogs.PromptForName(
                    "Save Material to Library", "Name for this material:", MaterialLibrary.ListNames());
                if (name == null) return;

                MaterialLibrary.Save(name, JsonHelper.Serialize(chosen["material"]));
                _dockable.PushMaterialLibrary();
            }
            catch (SysException ex)
            {
                MessageBox.Show("Could not save the material:\n\n" + ex.Message, "Ray Optics",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Purely native/local — the library's contents live entirely as files on disk,
        // so no round-trip to the web panel is needed to list or remove an entry, only
        // to notify the panel afterward that the dropdown contents changed.
        private void OnDeleteMaterial(NameValueMap context)
        {
            var names = MaterialLibrary.ListNames();
            if (names.Length == 0)
            {
                MessageBox.Show("No saved materials in your library.", "Ray Optics",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var chosen = PromptDialogs.PromptForChoice(
                "Delete Library Material", "Choose a material to delete:", names);
            if (chosen == null) return;

            var confirm = MessageBox.Show(
                $"Delete \"{chosen}\" from your material library? This cannot be undone.",
                "Ray Optics", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            MaterialLibrary.Delete(chosen);
            if (_dockable != null && _dockable.IsOpen) _dockable.PushMaterialLibrary();
        }

        // Snapshots live next to the active document (see SettingsStore), so an unsaved
        // document (no FullFileName yet) has nowhere to put them — caught explicitly
        // with a clear message rather than surfacing a raw IO exception.
        private async void OnSaveSettings(NameValueMap context)
        {
            if (!RequirePanelOpen()) return;
            Document doc = _inv.ActiveDocument;
            if (doc == null || string.IsNullOrEmpty(doc.FullFileName))
            {
                MessageBox.Show("Save the document first — settings are stored alongside its file.",
                    "Ray Optics", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                var snapshotJson = await _dockable.RequestAsync("requestSettingsSnapshot");
                var name = PromptDialogs.PromptForName(
                    "Save Settings", "Name for this settings snapshot:",
                    SettingsStore.ListNames(doc.FullFileName));
                if (name == null) return;

                SettingsStore.Save(doc.FullFileName, name, snapshotJson);
            }
            catch (SysException ex)
            {
                MessageBox.Show("Could not save settings:\n\n" + ex.Message, "Ray Optics",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnLoadSettings(NameValueMap context)
        {
            if (!RequirePanelOpen()) return;
            Document doc = _inv.ActiveDocument;
            if (doc == null || string.IsNullOrEmpty(doc.FullFileName))
            {
                MessageBox.Show("Save the document first — settings are stored alongside its file.",
                    "Ray Optics", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var names = SettingsStore.ListNames(doc.FullFileName);
            if (names.Length == 0)
            {
                MessageBox.Show("No saved settings for this document.", "Ray Optics",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var chosen = PromptDialogs.PromptForChoice(
                "Load Settings", "Choose a settings snapshot to load:", names);
            if (chosen == null) return;

            var json = SettingsStore.Load(doc.FullFileName, chosen);
            if (json == null) return;
            _dockable.PostApplySettings(json);
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
            if (_closeBtn != null)
            {
                _closeBtn.OnExecute -= OnClose;
                _closeBtn = null;
            }
            if (_newMaterialBtn != null)
            {
                _newMaterialBtn.OnExecute -= OnNewMaterial;
                _newMaterialBtn = null;
            }
            if (_deleteMaterialBtn != null)
            {
                _deleteMaterialBtn.OnExecute -= OnDeleteMaterial;
                _deleteMaterialBtn = null;
            }
            if (_saveSettingsBtn != null)
            {
                _saveSettingsBtn.OnExecute -= OnSaveSettings;
                _saveSettingsBtn = null;
            }
            if (_loadSettingsBtn != null)
            {
                _loadSettingsBtn.OnExecute -= OnLoadSettings;
                _loadSettingsBtn = null;
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
