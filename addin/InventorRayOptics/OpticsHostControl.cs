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
    /// </summary>
    public class OpticsHostControl : UserControl
    {
        public WebView2 Web { get; } = new WebView2 { Dock = DockStyle.Fill };

        public event EventHandler<string> MessageFromWeb;

        public OpticsHostControl()
        {
            Controls.Add(Web);
        }

        public async Task InitAsync(string webRootFolder)
        {
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

            Web.CoreWebView2.Navigate("https://app.local/index.html");
        }

        public void PostJson(string json)
        {
            Web.CoreWebView2?.PostWebMessageAsJson(json);
        }
    }
}
