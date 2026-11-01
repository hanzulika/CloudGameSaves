using SaveCloud.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SaveCloud.Platform
{
    /// <summary>
    /// Tells which games are running by matching running executables against games' install folders, and raises
    /// <see cref="GameExited"/> when the last process of a game closes. Polls every few seconds: cheaper and more
    /// reliable than WMI process-stop events, which need admin rights.
    /// </summary>
    public sealed class ProcessMonitor : IDisposable
    {
        private readonly object _lock = new object();
        private readonly Timer _timer;
        private readonly Dictionary<int, string> _pathByPid = new Dictionary<int, string>();
        private Dictionary<string, List<string>> _installs = new Dictionary<string, List<string>>();
        private HashSet<string> _running = new HashSet<string>(StringComparer.Ordinal);

        public event Action<string> GameExited;
        public event Action<string> GameStarted;

        public ProcessMonitor(TimeSpan interval)
        {
            _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, interval);
        }

        /// <summary>Game name to install folders, from the latest scan.</summary>
        public void SetInstallFolders(Dictionary<string, List<string>> installs)
        {
            lock (_lock)
            {
                _installs = installs.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Select(p => p.TrimEnd('\\') + "\\").ToList());
            }
        }

        public bool IsRunning(string game)
        {
            lock (_lock) return _running.Contains(game);
        }

        private int _polling;

        private void Poll()
        {
            if (Interlocked.Exchange(ref _polling, 1) == 1) return;
            try
            {
                var now = new HashSet<string>(StringComparer.Ordinal);
                Process[] procs = Process.GetProcesses();
                var alive = new HashSet<int>();
                Dictionary<string, List<string>> installs;
                lock (_lock) installs = _installs;

                foreach (var p in procs)
                {
                    using (p)
                    {
                        alive.Add(p.Id);
                        if (!_pathByPid.TryGetValue(p.Id, out string path))
                            _pathByPid[p.Id] = path = ImagePath(p.Id);
                        if (path == null) continue;

                        foreach (var kv in installs)
                            if (kv.Value.Any(dir => path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)))
                                now.Add(kv.Key);
                    }
                }
                foreach (var dead in _pathByPid.Keys.Where(pid => !alive.Contains(pid)).ToList())
                    _pathByPid.Remove(dead);

                HashSet<string> before;
                lock (_lock)
                {
                    before = _running;
                    _running = now;
                }
                foreach (var g in now.Where(g => !before.Contains(g)))
                {
                    Logger.Info($"Game started: {g}");
                    GameStarted?.Invoke(g);
                }
                foreach (var g in before.Where(g => !now.Contains(g)))
                {
                    Logger.Info($"Game closed: {g}");
                    GameExited?.Invoke(g);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Process check failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
            }
        }

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        /// <summary>Executable path without needing admin rights (works for most games; anti-cheat-protected ones may hide it).</summary>
        private static string ImagePath(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            catch (Win32Exception) { return null; }
            finally { CloseHandle(h); }
        }

        public void Dispose() => _timer.Dispose();
    }
}
