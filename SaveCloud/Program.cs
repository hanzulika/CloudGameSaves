using SaveCloud.Core;
using SaveCloud.UI;
using System;
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
            string user = Environment.UserName;
            // Launching SaveCloud while it's already running just brings its window up.
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SaveCloud-Show-" + user))
            using (var mutex = new Mutex(true, @"Local\SaveCloud-" + user, out bool first))
            {
                if (!first)
                {
                    show.Set();
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Logger.Error("Unhandled UI error", e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                    Logger.Error("Unhandled error", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));

                bool background = args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));
                Logger.Info($"SaveCloud {typeof(Program).Assembly.GetName().Version.ToString(3)} starting{(background ? " in background" : "")} on {Environment.MachineName}");

                var app = new TrayApp(background);
                new Thread(() =>
                {
                    while (true)
                    {
                        show.WaitOne();
                        app.ShowWindow();
                    }
                }) { IsBackground = true, Name = "SaveCloud show signal" }.Start();

                Application.Run(app);
                GC.KeepAlive(mutex);
            }
        }
    }
}
