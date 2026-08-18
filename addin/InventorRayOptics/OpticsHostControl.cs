using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace InventorRayOptics
{
    /// <summary>
    /// WinForms host for the WebView2 control that is parented into an Inventor
    /// DockableWindow via DockableWindow.AddChild(hwnd).
    ///
    /// This control's own HWND is handed to Inventor exactly once and must stay valid for
    /// the rest of the session (Inventor gives no way to detach or re-parent a dockable
    /// window's child). The WebView2 *inside* it, by contrast, is created in InitAsync and
    /// destroyed in ShutdownWeb, so closing the Ray Optics environment can genuinely
    /// release the browser and all page state while leaving the host shell intact for a
    /// later reopen.
    /// </summary>
    public class OpticsHostControl : UserControl
    {
        public WebView2 Web { get; private set; }

        public event EventHandler<string> MessageFromWeb;

        public async Task InitAsync(string webRootFolder)
        {
            ShutdownWeb(); // discard any previous session's browser before making a new one

            Web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(Web);

            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "InventorRayOptics", "WebView2UserData"));
            await Web.EnsureCoreWebView2Async(env);

            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.local", webRootFolder, CoreWebView2HostResourceAccessKind.Allow);
            Web.CoreWebView2.Settings.AreDevToolsEnabled = true; // F12 for web-side debugging
            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Web.CoreWebView2.WebMessageReceived += (s, e) =>
                MessageFromWeb?.Invoke(this, e.TryGetWebMessageAsString());

            // Navigate() does not wait for the page to finish loading — posting a
            // message immediately after it returns can race the page's own
            // WebMessageReceived listener (js/app.js attaches it at module-eval time),
            // silently dropping the message. Wait for NavigationCompleted so callers of
            // InitAsync can safely PostJson right away.
            var navigationDone = new TaskCompletionSource<bool>();
            void OnNavigationCompleted(object s, CoreWebView2NavigationCompletedEventArgs e)
            {
                Web.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                navigationDone.TrySetResult(e.IsSuccess);
            }
            Web.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            Web.CoreWebView2.Navigate("https://app.local/index.html");
            await navigationDone.Task;
        }

        /// <summary>Destroys the browser and everything loaded in it, keeping this
        /// control (and therefore the HWND Inventor holds) alive.</summary>
        public void ShutdownWeb()
        {
            if (Web == null) return;
            Controls.Remove(Web);
            Web.Dispose();
            Web = null;
        }

        public void PostJson(string json)
        {
            Web?.CoreWebView2?.PostWebMessageAsJson(json);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ShutdownWeb();
            base.Dispose(disposing);
        }
    }
}
