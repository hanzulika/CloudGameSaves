using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json.Linq;
using SaveCloud.Core;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SaveCloud.UI
{
    /// <summary>The main window: hosts the HTML interface in web\ through WebView2.</summary>
    public sealed class MainWindow : Form
    {
        private const string HostName = "app.savecloud";
        private readonly WebView2 _web;
        private readonly Action<JObject> _onCommand;
        private string _pendingState;
        private bool _ready;

        public MainWindow(Action<JObject> onCommand)
        {
            _onCommand = onCommand;
            Text = "SaveCloud";
            Icon = AppIcon.Load();
            BackColor = Color.FromArgb(11, 13, 20);
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1180, 780);
            MinimumSize = new Size(560, 480);

            _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(_web);
            HandleCreated += (s, e) => UseDarkTitleBar();
            Load += async (s, e) => await InitAsync();
        }

        private async System.Threading.Tasks.Task InitAsync()
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.CacheDir, "WebView2"));
                await _web.EnsureCoreWebView2Async(env);
                var core = _web.CoreWebView2;
                core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.SetVirtualHostNameToFolderMapping(HostName, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web"),
                                                       CoreWebView2HostResourceAccessKind.DenyCors);
                core.WebMessageReceived += (s, e) =>
                {
                    try { _onCommand(JObject.Parse(e.WebMessageAsJson)); }
                    catch (Exception ex) { Logger.Error("UI command failed", ex); }
                };
                // Links (e.g. Tailscale download) open in the real browser, never inside the app.
                core.NewWindowRequested += (s, e) => { e.Handled = true; OpenUrl(e.Uri); };
                core.NavigationStarting += (s, e) =>
                {
                    if (!e.Uri.StartsWith($"https://{HostName}/", StringComparison.OrdinalIgnoreCase)) { e.Cancel = true; OpenUrl(e.Uri); }
                };
                core.NavigationCompleted += (s, e) =>
                {
                    _ready = true;
                    if (_pendingState != null) core.PostWebMessageAsJson(_pendingState);
                };
                core.Navigate($"https://{HostName}/index.html");
            }
            catch (Exception ex) when (ex is WebView2RuntimeNotFoundException || ex is COMException || ex is FileNotFoundException)
            {
                Logger.Error("WebView2 is unavailable", ex);
                ShowRuntimeMissing();
            }
        }

        /// <summary>Sends a JSON message ({type, ...}) to the page. Safe to call from any thread.</summary>
        public void Post(string json)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((Action)(() => Post(json))); } catch (InvalidOperationException) { }
                return;
            }
            if (json.Contains("\"type\":\"state\"")) _pendingState = json;
            if (_ready && _web.CoreWebView2 != null) _web.CoreWebView2.PostWebMessageAsJson(json);
        }

        public void ShowAndActivate()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Closing the window keeps SaveCloud running in the tray.
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        private void ShowRuntimeMissing()
        {
            Controls.Clear();
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                Padding = new Padding(40),
                BackColor = BackColor,
            };
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(600, 0),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 11f),
                Text = "SaveCloud's window needs the Microsoft Edge WebView2 Runtime, which is missing on this PC. " +
                       "Syncing keeps working in the background. Install the runtime, then reopen this window.",
            });
            var link = new LinkLabel { AutoSize = true, Text = "Download WebView2 Runtime", Font = new Font("Segoe UI", 11f), LinkColor = Color.FromArgb(138, 156, 255) };
            link.LinkClicked += (s, e) => OpenUrl("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
            panel.Controls.Add(link);
            Controls.Add(panel);
        }

        public static void OpenUrl(string url)
        {
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            try { Process.Start(url); } catch (Exception ex) { Logger.Warn($"Could not open {url}: {ex.Message}"); }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private void UseDarkTitleBar()
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref on, 4);
            }
            catch { /* older Windows: default title bar */ }
        }
    }

    internal static class AppIcon
    {
        public static Icon Load()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; }
            catch { return SystemIcons.Application; }
        }
    }
}
