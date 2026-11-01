using SaveCloud.Core;
using SaveCloud.Core.Remote;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SaveCloud.Platform
{
    /// <summary>Reads the local Tailscale client's state via its CLI (installed with the Windows app).</summary>
    public static class TailscaleCli
    {
        public const string DownloadUrl = "https://tailscale.com/download/windows";

        private static string InstallDir()
        {
            // ProgramW6432 is the real Program Files even from a 32-bit process.
            foreach (var root in new[]
            {
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string dir = Path.Combine(root, "Tailscale");
                if (File.Exists(Path.Combine(dir, "tailscale.exe"))) return dir;
            }
            return null;
        }

        public static TailscaleStatus GetStatus()
        {
            string dir = InstallDir();
            if (dir == null) return new TailscaleStatus { State = TailscaleState.NotInstalled };

            try
            {
                var psi = new ProcessStartInfo(Path.Combine(dir, "tailscale.exe"), "status --json")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    var stdout = p.StandardOutput.ReadToEndAsync();
                    var stderr = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); } catch { }
                        return new TailscaleStatus { State = TailscaleState.NotRunning, Error = "tailscale status timed out" };
                    }
                    string json = stdout.Result;
                    if (json.TrimStart().StartsWith("{")) return TailscaleStatus.Parse(json);
                    // "failed to connect to local Tailscale service" and similar: the service isn't running.
                    return new TailscaleStatus { State = TailscaleState.NotRunning, Error = (stderr.Result + json).Trim() };
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not read Tailscale status: {ex.Message}");
                return new TailscaleStatus { State = TailscaleState.NotRunning, Error = ex.Message };
            }
        }

        /// <summary>Opens the Tailscale app so the user can sign in or connect.</summary>
        public static void OpenApp()
        {
            string dir = InstallDir();
            string ipn = dir == null ? null : Path.Combine(dir, "tailscale-ipn.exe");
            try
            {
                if (ipn != null && File.Exists(ipn)) Process.Start(ipn);
                else Process.Start(DownloadUrl);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not open Tailscale: {ex.Message}");
            }
        }
    }
}
