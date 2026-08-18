using System;
using System.Collections.Generic;
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
        private readonly Dictionary<string, TaskCompletionSource<string>> _pendingRequests =
            new Dictionary<string, TaskCompletionSource<string>>();

        private string WebRoot => IOPath.Combine(
            IOPath.GetDirectoryName(typeof(OpticsDockable).Assembly.Location) ?? ".", "webapp");

        public OpticsDockable(Inventor.Application inv)
        {
            _inv = inv;
        }

        public bool IsOpen => _dw != null && _dw.Visible;

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

                // Best-effort — only part documents have a flat body-name list (see
                // TryGetBodyNames); anything else falls back to "Body 1", "Body 2", ...
                var namesPath = IOPath.Combine(WebRoot, "bodyNames.json");
                var bodyNames = StepExporter.TryGetBodyNames(doc);
                if (bodyNames != null) IOFile.WriteAllText(namesPath, JsonHelper.Serialize(bodyNames));
                else if (IOFile.Exists(namesPath)) IOFile.Delete(namesPath); // stale names from a previous document

                _host.PostJson("{\"type\":\"loadStep\",\"url\":\"https://app.local/model.step?t=" +
                    DateTime.UtcNow.Ticks + "\",\"version\":\"" + BuildInfo.Version + "\"}"); // cache-bust so re-launch always reloads
                PushMaterialLibrary();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Ray Optics could not open the panel:\n\n" + ex,
                    "Ray Optics", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>Pushes the current on-disk material library down to the web panel so
        /// every body's material dropdown picks up additions/removals immediately.</summary>
        public void PushMaterialLibrary()
        {
            _host?.PostJson("{\"type\":\"materialLibraryUpdated\",\"library\":" +
                MaterialLibrary.LoadAllAsJsonObject() + "}");
        }

        /// <summary>Sends a request-typed message to the web panel and awaits its reply
        /// (matched by requestId — see OnMessageFromWeb and app.js's postReplyToHost).
        /// Returns the reply's raw "data" payload as JSON text.</summary>
        public async Task<string> RequestAsync(string requestType, int timeoutMs = 5000)
        {
            var requestId = Guid.NewGuid().ToString();
            var tcs = new TaskCompletionSource<string>();
            _pendingRequests[requestId] = tcs;
            _host.PostJson(JsonHelper.Serialize(new Dictionary<string, object>
            {
                ["type"] = requestType,
                ["requestId"] = requestId,
            }));
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            _pendingRequests.Remove(requestId);
            if (completed != tcs.Task)
                throw new TimeoutException("The Ray Optics panel did not respond in time.");
            return await tcs.Task;
        }

        /// <summary>Fire-and-forget push of a previously-saved settings snapshot for the
        /// web panel to apply (see app.js's applySettings). `json` is trusted to already
        /// be valid JSON — it always comes from a file this add-in itself wrote.</summary>
        public void PostApplySettings(string json)
        {
            _host?.PostJson("{\"type\":\"applySettings\",\"data\":" + json + "}");
        }

        private const string DockWindowInternalName = "IROptics:Dock";

        private void EnsureDockableWindow()
        {
            if (_dw != null) return;

            _host = new OpticsHostControl();
            _host.CreateControl(); // force HWND creation before AddChild needs it
            _host.MessageFromWeb += OnMessageFromWeb;

            var uiMgr = _inv.UserInterfaceManager;

            // A DockableWindow persists for the whole Inventor session, so calling Add
            // again with an internal name that already exists fails with E_INVALIDARG
            // (hit when the add-in is unloaded/reloaded, since that builds a fresh
            // OpticsDockable while Inventor still holds the original window). Reuse the
            // existing one when it's there, and only configure docking on first create so
            // a reopen doesn't yank the panel back to a size the user has since changed.
            bool created = false;
            try { _dw = uiMgr.DockableWindows[DockWindowInternalName]; }
            catch { _dw = null; }
            if (_dw == null)
            {
                _dw = uiMgr.DockableWindows.Add(
                    StandardAddInServer.AddInClientId, DockWindowInternalName, "Ray Optics");
                created = true;
            }

            _dw.AddChild(_host.Handle.ToInt32());
            if (created)
            {
                _dw.DockingState = DockingStateEnum.kDockRight;
                // default docking width (230px) only fits the sidebar, hiding the 3D
                // viewport entirely — give it room to show both on first use
                _dw.Width = 900;
            }
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

        // Every reply the web panel sends back is {requestId, data} (see app.js's
        // postReplyToHost) — match it to whichever RequestAsync call is waiting on that
        // id and resolve it with the "data" payload re-serialized as JSON text. Messages
        // with no matching pending request (or no requestId at all) are silently ignored;
        // nothing today sends unsolicited web->host messages outside this reply pattern.
        private void OnMessageFromWeb(object sender, string json)
        {
            var parsed = JsonHelper.Deserialize(json) as Dictionary<string, object>;
            if (parsed == null) return;
            if (!parsed.TryGetValue("requestId", out var ridObj) || !(ridObj is string requestId)) return;
            if (!_pendingRequests.TryGetValue(requestId, out var tcs)) return;

            parsed.TryGetValue("data", out var data);
            tcs.TrySetResult(JsonHelper.Serialize(data));
        }

        /// <summary>
        /// Closes the panel for the "Close Ray Optics" ribbon button: hides the window and
        /// destroys the browser along with every bit of scene/light/material state in it,
        /// so reopening starts clean. The DockableWindow and host control deliberately
        /// survive — Inventor owns the window for the session and offers no way to remove
        /// it or detach its child, so tearing those down is what previously made a reopen
        /// throw E_INVALIDARG.
        /// </summary>
        public void CloseEnvironment()
        {
            if (_dw != null) _dw.Visible = false;
            _host?.ShutdownWeb();
            _initTask = null; // force a fresh InitAsync (and re-navigation) on next show

            // nothing will ever answer these now that the page is gone; fail them
            // immediately instead of making each caller wait out its timeout
            foreach (var pending in _pendingRequests.Values)
                pending.TrySetException(new InvalidOperationException("The Ray Optics panel was closed."));
            _pendingRequests.Clear();
        }

        public void Dispose()
        {
            CloseEnvironment();
            if (_host != null)
            {
                _host.MessageFromWeb -= OnMessageFromWeb;
                _host.Dispose();
                _host = null;
            }
            _dw = null;
        }
    }
}
