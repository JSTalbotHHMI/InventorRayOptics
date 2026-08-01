using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using Inventor;
using IOPath = System.IO.Path;
using IOFile = System.IO.File;
using IODirectory = System.IO.Directory;

namespace InventorRayOptics
{
    /// <summary>
    /// Owns the dockable panel and its WebView2 host, and drives the
    /// export-STEP -> load-in-webapp sequence each time it is shown.
    /// </summary>
    public class OpticsDockable : IDisposable
    {
        private readonly Inventor.Application _inv;
        private DockableWindow _dw;
        private OpticsHostControl _host;
        private Task _initTask;

        private string WebRoot => IOPath.Combine(
            IOPath.GetDirectoryName(typeof(OpticsDockable).Assembly.Location) ?? ".", "webapp");

        public OpticsDockable(Inventor.Application inv)
        {
            _inv = inv;
        }

        public async void ShowFor(Document doc)
        {
            try
            {
                EnsureDockableWindow();
                await EnsureInitializedAsync();
                _dw.Visible = true;

                var stepPath = StepExporter.ExportActive(_inv, doc);
                var destPath = IOPath.Combine(WebRoot, "model.step");
                IOFile.Copy(stepPath, destPath, true);

                _host.PostJson("{\"type\":\"loadStep\",\"url\":\"https://app.local/model.step?t=" +
                    DateTime.UtcNow.Ticks + "\"}"); // cache-bust so re-launch always reloads
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Ray Optics could not open the panel:\n\n" + ex,
                    "Ray Optics", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void EnsureDockableWindow()
        {
            if (_dw != null) return;

            _host = new OpticsHostControl();
            _host.CreateControl(); // force HWND creation before AddChild needs it
            _host.MessageFromWeb += OnMessageFromWeb;

            var uiMgr = _inv.UserInterfaceManager;
            _dw = uiMgr.DockableWindows.Add(
                StandardAddInServer.AddInClientId, "IROptics:Dock", "Ray Optics");
            _dw.AddChild(_host.Handle.ToInt32());
            _dw.DockingState = DockingStateEnum.kDockRight;
            // default docking width (230px) only fits the sidebar, hiding the 3D
            // viewport entirely — give it room to show both on first use
            _dw.Width = 900;
        }

        private Task EnsureInitializedAsync()
        {
            if (_initTask == null)
            {
                IODirectory.CreateDirectory(WebRoot);
                _initTask = _host.InitAsync(WebRoot);
            }
            return _initTask;
        }

        private void OnMessageFromWeb(object sender, string json)
        {
            // Reserved for future two-way selection (Phase 4). No-op for now.
        }

        public void Dispose()
        {
            if (_dw != null)
            {
                _dw.Visible = false;
            }
            if (_host != null)
            {
                _host.MessageFromWeb -= OnMessageFromWeb;
                _host.Dispose();
                _host = null;
            }
        }
    }
}
