using SaveCloud.Core;
using SaveCloud.Platform;
using SaveCloud.UI;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace SaveCloud
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // One copy per Windows user; a second launch (e.g. from the Start menu) just exits.
            using (var mutex = new Mutex(true, @"Local\SaveCloud-" + Environment.UserName, out bool first))
            {
                if (!first)
                {
                    MessageBox.Show("SaveCloud is already running – look for its icon in the system tray.", "SaveCloud",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Logger.Error("Unhandled UI error", e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                    Logger.Error("Unhandled error", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));

                ImportLegacyEnv();
                bool background = args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));
                Logger.Info($"SaveCloud starting{(background ? " in background" : "")} on {Environment.MachineName}");
                Application.Run(new TrayApp(background));
            }
        }

        /// <summary>One-time import of the old .env (SAVECLOUD_HOST etc.) into the new config, if there's no config yet.</summary>
        private static void ImportLegacyEnv()
        {
            try
            {
                string env = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env");
                if (File.Exists(AppPaths.ConfigFile) || !File.Exists(env)) return;

                var values = File.ReadAllLines(env)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("#") && l.IndexOf('=') > 0)
                    .ToDictionary(l => l.Substring(0, l.IndexOf('=')).Trim(), l => l.Substring(l.IndexOf('=') + 1).Trim().Trim('"'));

                var config = new AppConfig();
                if (values.TryGetValue("SAVECLOUD_HOST", out var host)) config.Host = host;
                if (values.TryGetValue("SAVECLOUD_USERNAME", out var user)) config.Username = user;
                if (values.TryGetValue("SAVECLOUD_PASSWORD", out var pw)) config.PasswordProtected = Secrets.Protect(pw);
                config.Save();
                Logger.Info("Imported server settings from the old .env file; review them in Settings (server folder now defaults to /data)");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not import .env: {ex.Message}");
            }
        }
    }
}
