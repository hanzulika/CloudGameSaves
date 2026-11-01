using SaveCloud.Core;
using SaveCloud.Core.Manifest;
using SaveCloud.Core.Remote;
using SaveCloud.Core.Scan;
using SaveCloud.Core.Sync;
using SaveCloud.Platform;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SaveCloud.UI
{
    /// <summary>
    /// The background app: lives in the tray, restores newer saves at startup, uploads when a game closes,
    /// and checks periodically for anything missed.
    /// </summary>
    public sealed class TrayApp : ApplicationContext
    {
        private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan FullRescanEvery = TimeSpan.FromHours(6);

        private readonly Control _ui;
        private readonly NotifyIcon _tray;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly SemaphoreSlim _work = new SemaphoreSlim(1, 1);

        private AppConfig _config;
        private SyncEngine _engine;
        private GameScanner _scanner;
        private ProcessMonitor _processes;
        private List<ManifestGame> _manifest = new List<ManifestGame>();
        private StatusForm _status;
        private System.Threading.Timer _scanTimer, _pullTimer;
        private DateTime _lastFullScan = DateTime.MinValue;
        private bool _serverErrorShown;
        private string _lastBalloonGame;

        public TrayApp(bool background)
        {
            _ui = new Control();
            _ui.CreateControl(); // gives us a handle to marshal background work back to the UI thread

            var menu = new ContextMenuStrip();
            menu.Items.Add("Status", null, (s, e) => ShowStatus());
            menu.Items.Add("Sync now", null, (s, e) => _ = RunAsync("Sync", () => FullSync()));
            menu.Items.Add("Rescan for games", null, (s, e) => _ = RunAsync("Rescan", () => { Rescan(force: true); FullSync(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Settings…", null, (s, e) => ShowSettings());
            menu.Items.Add("Open log folder", null, (s, e) => Process.Start("explorer.exe", AppPaths.LogDir));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitThread());

            _tray = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "SaveCloud",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _tray.DoubleClick += (s, e) => ShowStatus();
            _tray.BalloonTipClicked += (s, e) => ShowStatus(_lastBalloonGame);

            Logger.Prune();
            _config = AppConfig.Load();
            StartupRegistration.Apply(_config.StartWithWindows);

            if (!_config.IsServerConfigured)
            {
                Balloon("SaveCloud needs setup", "Enter your save server details to start syncing.", ToolTipIcon.Info);
                ShowSettings();
            }
            else if (!background)
            {
                ShowStatus();
            }

            _ = Task.Run(StartupAsync);
        }

        private static Icon LoadIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; }
            catch { return SystemIcons.Application; }
        }

        // ---- lifecycle ---------------------------------------------------------------------------------

        private async Task StartupAsync()
        {
            try
            {
                _manifest = await ManifestLoader.LoadAsync(_cts.Token).ConfigureAwait(false);
                if (_manifest.Count == 0)
                {
                    Balloon("SaveCloud", "Couldn't download the game list. Check your internet connection; retrying later.", ToolTipIcon.Warning);
                }

                Rebuild();
                await RunAsync("Startup sync", () =>
                {
                    Rescan(force: true);
                    // The network can lag behind sign-in, so retry the first sync for a few minutes.
                    for (int attempt = 0; attempt < 10 && !_cts.IsCancellationRequested; attempt++)
                    {
                        if (FullSync(isStartup: true)) break;
                        _cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                    }
                }).ConfigureAwait(false);

                _scanTimer = new System.Threading.Timer(_ => _ = RunAsync("Change check", CheckLocalChanges, skipIfBusy: true), null,
                    TimeSpan.FromMinutes(Math.Max(1, _config.ScanIntervalMinutes)), TimeSpan.FromMinutes(Math.Max(1, _config.ScanIntervalMinutes)));
                _pullTimer = new System.Threading.Timer(_ => _ = RunAsync("Periodic sync", () => { Rescan(force: false); FullSync(); }, skipIfBusy: true), null,
                    TimeSpan.FromMinutes(Math.Max(5, _config.PullIntervalMinutes)), TimeSpan.FromMinutes(Math.Max(5, _config.PullIntervalMinutes)));
            }
            catch (Exception ex)
            {
                Logger.Error("Startup failed", ex);
            }
        }

        /// <summary>(Re)creates the engine after settings change.</summary>
        private void Rebuild()
        {
            var env = WindowsEnvironment.Build(_config);
            _scanner = new GameScanner(env, _config);

            _processes?.Dispose();
            _processes = new ProcessMonitor(TimeSpan.FromSeconds(5));
            _processes.GameExited += OnGameExited;

            _engine = new SyncEngine(_config, CreateTransport, _scanner, _engine?.State ?? SyncState.Load(), _processes.IsRunning);
            _engine.ConflictFound += (game, c) => Balloon("Save conflict: " + game,
                $"Changed on this PC and on {c.RemoteMachine}. Click to choose which save to keep.", ToolTipIcon.Warning, game);
            _engine.Changed += () => OnUi(() => _status?.RefreshGames());
            _lastFullScan = DateTime.MinValue;
        }

        private IRemoteTransport CreateTransport()
        {
            var t = new SftpTransport(
                _config.Host, _config.Port, _config.Username,
                string.IsNullOrWhiteSpace(_config.PrivateKeyPath) ? null : _config.PrivateKeyPath,
                Secrets.Unprotect(_config.PrivateKeyPassphraseProtected),
                Secrets.Unprotect(_config.PasswordProtected),
                _config.RemoteRoot, _config.HostKeyFingerprint);
            t.HostKeyFirstSeen += fp =>
            {
                Logger.Info($"Pinned server host key SHA256:{fp}");
                _config.HostKeyFingerprint = fp;
                _config.Save();
            };
            return t;
        }

        private void Rescan(bool force)
        {
            if (_manifest.Count == 0)
            {
                _manifest = ManifestLoader.LoadAsync(_cts.Token).GetAwaiter().GetResult();
                if (_manifest.Count == 0) return;
            }
            if (!force && DateTime.UtcNow - _lastFullScan < FullRescanEvery) return;

            if (force) Rebuild(); // picks up newly installed Steam libraries, settings changes, etc.
            var scan = _scanner.ScanAll(_manifest, _cts.Token);
            _engine.UpdateLocal(_manifest, scan);
            _processes.SetInstallFolders(scan.InstallPaths);
            _lastFullScan = DateTime.UtcNow;
            foreach (var s in scan.Skipped) Logger.Info("Skipped " + s);
            OnUi(() => _status?.RefreshGames());
        }

        /// <summary>Full two-way sync. Returns false if the server couldn't be reached.</summary>
        private bool FullSync() => FullSync(false);

        private bool FullSync(bool isStartup)
        {
            if (!_config.IsServerConfigured || _engine == null) return true;
            try
            {
                var results = _engine.SyncAll(ct: _cts.Token);
                _serverErrorShown = false;
                Summarize(results, isStartup);
                return true;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Logger.Error("Sync failed", ex);
                if (!_serverErrorShown)
                {
                    _serverErrorShown = true;
                    Balloon("Save server unreachable", ex.Message, ToolTipIcon.Warning);
                }
                return false;
            }
        }

        private void CheckLocalChanges()
        {
            if (!_config.IsServerConfigured || _engine == null) return;
            foreach (var game in _engine.FindLocalChanges(QuietPeriod))
                SyncOne(game);
        }

        private void OnGameExited(string game)
        {
            // Give the game a moment to finish flushing its save files after the process ends.
            _ = Task.Delay(TimeSpan.FromSeconds(10), _cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) _ = RunAsync("Upload after " + game, () => SyncOne(game));
            }, TaskScheduler.Default);
        }

        private void SyncOne(string game)
        {
            try
            {
                var r = _engine.SyncGame(game);
                Logger.Info(r.ToString());
                if (r.Action == SyncAction.Uploaded) SetTrayText($"SaveCloud – uploaded {game}");
                if (r.Action == SyncAction.Error) Balloon("Couldn't sync " + game, r.Message, ToolTipIcon.Warning, game);
            }
            catch (Exception ex)
            {
                Logger.Error($"Sync of {game} failed", ex);
            }
        }

        private void Summarize(List<GameSyncResult> results, bool isStartup)
        {
            var down = results.Where(r => r.Action == SyncAction.Downloaded).Select(r => r.Game).ToList();
            var up = results.Where(r => r.Action == SyncAction.Uploaded).Select(r => r.Game).ToList();
            var errors = results.Where(r => r.Action == SyncAction.Error).ToList();

            foreach (var r in results.Where(r => r.Action != SyncAction.InSync)) Logger.Info(r.ToString());

            if (down.Count > 0)
                Balloon(isStartup ? "Restored newer saves" : "Downloaded newer saves", Short(down), ToolTipIcon.Info);
            if (errors.Count > 0)
                Balloon($"{errors.Count} game(s) failed to sync", Short(errors.Select(e => e.Game).ToList()) + " – see Status", ToolTipIcon.Warning);

            SetTrayText($"SaveCloud – {results.Count(r => r.Action == SyncAction.InSync || r.Action == SyncAction.Uploaded || r.Action == SyncAction.Downloaded)} games synced"
                        + (up.Count + down.Count > 0 ? $" ({up.Count}↑ {down.Count}↓)" : ""));
        }

        private static string Short(List<string> names)
            => names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + $" and {names.Count - 3} more";

        /// <summary>Runs work on a background thread, one job at a time.</summary>
        private async Task RunAsync(string what, Action work, bool skipIfBusy = false)
        {
            if (_cts.IsCancellationRequested) return;
            if (skipIfBusy)
            {
                // Timer ticks shouldn't pile up behind a long sync; the next tick will do the same work.
                if (!await _work.WaitAsync(0).ConfigureAwait(false)) return;
            }
            else
            {
                try { await _work.WaitAsync(_cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            try
            {
                OnUi(() => _status?.SetBusy(what));
                await Task.Run(work, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error(what + " failed", ex);
            }
            finally
            {
                _work.Release();
                OnUi(() => _status?.SetBusy(null));
            }
        }

        // ---- UI ----------------------------------------------------------------------------------------

        private void ShowStatus(string selectGame = null)
        {
            if (_status == null || _status.IsDisposed)
            {
                _status = new StatusForm(() => _engine,
                    syncNow: () => _ = RunAsync("Sync", () => FullSync()),
                    resolve: (game, keepLocal) => _ = RunAsync("Resolve " + game, () =>
                    {
                        var r = _engine.Resolve(game, keepLocal);
                        Logger.Info("Resolved: " + r);
                        if (r.Action == SyncAction.Error || r.Action == SyncAction.Deferred)
                            OnUi(() => MessageBox.Show(_status, r.Message, "SaveCloud", MessageBoxButtons.OK, MessageBoxIcon.Warning));
                    }),
                    ignore: game =>
                    {
                        if (!_config.IgnoredGames.Contains(game)) _config.IgnoredGames.Add(game);
                        _config.Save();
                        _ = RunAsync("Rescan", () => Rescan(force: true));
                    });
            }
            _status.Show();
            _status.WindowState = FormWindowState.Normal;
            _status.Activate();
            if (selectGame != null) _status.SelectGame(selectGame);
        }

        private void ShowSettings()
        {
            using (var f = new SettingsForm(_config))
            {
                if (f.ShowDialog() != DialogResult.OK) return;
            }
            _config.Save();
            StartupRegistration.Apply(_config.StartWithWindows);
            _ = RunAsync("Apply settings", () =>
            {
                if (_manifest.Count == 0) return; // startup is still loading; it will use the new settings
                Rescan(force: true);
                FullSync();
            });
        }

        private void Balloon(string title, string text, ToolTipIcon icon, string game = null)
        {
            OnUi(() =>
            {
                _lastBalloonGame = game;
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = string.IsNullOrEmpty(text) ? " " : text;
                _tray.BalloonTipIcon = icon;
                _tray.ShowBalloonTip(5000);
            });
        }

        private void SetTrayText(string text)
            => OnUi(() => _tray.Text = text.Length > 63 ? text.Substring(0, 63) : text); // NotifyIcon limit

        private void OnUi(Action a)
        {
            if (_ui.IsDisposed) return;
            try
            {
                if (_ui.InvokeRequired) _ui.BeginInvoke(a);
                else a();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        protected override void ExitThreadCore()
        {
            _cts.Cancel();
            _scanTimer?.Dispose();
            _pullTimer?.Dispose();
            _processes?.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _status?.Dispose();
            _ui.Dispose();
            base.ExitThreadCore();
        }
    }
}
