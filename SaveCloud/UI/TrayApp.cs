using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SaveCloud.Core;
using SaveCloud.Core.Manifest;
using SaveCloud.Core.Remote;
using SaveCloud.Core.Scan;
using SaveCloud.Core.Sync;
using SaveCloud.Platform;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SaveCloud.UI
{
    /// <summary>
    /// The background app. One worker loop does everything that touches disk or network, in order:
    /// find the server over Tailscale, restore newer saves at sign-in, upload when a game closes, and check
    /// periodically for anything missed. The window and tray only queue work and show state.
    /// </summary>
    public sealed class TrayApp : ApplicationContext
    {
        private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan FullRescanEvery = TimeSpan.FromHours(6);
        private static readonly TimeSpan RediscoverEvery = TimeSpan.FromSeconds(20);

        private readonly Control _ui;
        private readonly NotifyIcon _tray;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly AutoResetEvent _kick = new AutoResetEvent(false);
        private readonly ConcurrentQueue<(string what, Action work)> _queue = new ConcurrentQueue<(string, Action)>();
        private readonly AppConfig _config;
        private readonly SyncState _state;
        private readonly bool _firstRun;

        private MainWindow _window;
        private List<ManifestGame> _manifest = new List<ManifestGame>();
        private GameScanner _scanner;
        private SyncEngine _engine;
        private ProcessMonitor _processes;

        // Connection (written by the worker, read by the UI).
        private volatile string _connState = "searching";
        private volatile string _serverUrl;
        private volatile string _login, _tsLogin, _mode, _connMessage;
        private volatile string _busy;
        private volatile bool _scanning = true;
        private DateTime? _lastSyncUtc;
        private bool _notAuthorizedShown, _offlineShown;

        public TrayApp(bool background)
        {
            _ui = new Control();
            _ui.CreateControl();

            _firstRun = !File.Exists(AppPaths.ConfigFile);
            _config = AppConfig.Load();
            if (_firstRun) _config.Save();
            _state = SyncState.Load();
            Logger.Prune();
            StartupRegistration.Apply(_config.StartWithWindows);

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open SaveCloud", null, (s, e) => ShowWindow());
            menu.Items.Add("Sync now", null, (s, e) => Enqueue("Syncing", () => { Rescan(); FullSync(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit SaveCloud", null, (s, e) => ExitThread());
            _tray = new NotifyIcon { Icon = AppIcon.Load(), Text = "SaveCloud", Visible = true, ContextMenuStrip = menu };
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
            _tray.BalloonTipClicked += (s, e) => ShowWindow();

            _processes = new ProcessMonitor(TimeSpan.FromSeconds(5));
            _processes.GameExited += game =>
                Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => Enqueue($"Uploading {game}", () => SyncOne(game)));
            _processes.GameStarted += _ => PushState();

            if (!background || _firstRun || string.IsNullOrEmpty(_config.LastServerUrl)) ShowWindow();
            new Thread(Worker) { IsBackground = true, Name = "SaveCloud worker" }.Start();
        }

        // ---- worker -------------------------------------------------------------------------------------

        private void Worker()
        {
            var now = DateTime.UtcNow;
            DateTime nextDiscover = now, nextPull = now, nextScan = now.AddMinutes(_config.ScanIntervalMinutes),
                     nextRescan = DateTime.MinValue, nextManifest = now;

            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    now = DateTime.UtcNow;

                    // Connection first, so the setup screen updates before the slow first game-list download.
                    if (_connState != "connected" && now >= nextDiscover)
                    {
                        Discover();
                        nextDiscover = now + RediscoverEvery;
                        if (_connState == "connected") nextPull = now; // just (re)connected: sync right away
                    }

                    if (_manifest.Count == 0 && now >= nextManifest)
                    {
                        Busy("Loading game list", () => _manifest = ManifestLoader.LoadAsync(_cts.Token).GetAwaiter().GetResult());
                        nextManifest = now.AddMinutes(5);
                        if (_manifest.Count > 0) nextRescan = DateTime.MinValue;
                    }
                    if (_manifest.Count > 0 && now >= nextRescan)
                    {
                        Busy("Looking for games", () => Rescan());
                        nextRescan = now + FullRescanEvery;
                    }

                    while (_queue.TryDequeue(out var job))
                    {
                        try { Busy(job.what, job.work); }
                        catch (Exception ex) { Logger.Error(job.what + " failed", ex); }
                    }

                    if (_connState == "connected" && _engine != null)
                    {
                        if (now >= nextPull)
                        {
                            Busy("Syncing", () => FullSync());
                            nextPull = DateTime.UtcNow.AddMinutes(Math.Max(2, _config.PullIntervalMinutes));
                            nextScan = DateTime.UtcNow.AddMinutes(Math.Max(1, _config.ScanIntervalMinutes));
                        }
                        else if (now >= nextScan)
                        {
                            foreach (var game in _engine.FindLocalChanges(QuietPeriod)) Busy($"Uploading {game}", () => SyncOne(game));
                            nextScan = DateTime.UtcNow.AddMinutes(Math.Max(1, _config.ScanIntervalMinutes));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Background loop error", ex);
                }
                WaitHandle.WaitAny(new[] { _kick, _cts.Token.WaitHandle }, TimeSpan.FromSeconds(5));
            }
        }

        private void Enqueue(string what, Action work)
        {
            _queue.Enqueue((what, work));
            _kick.Set();
        }

        private void Busy(string what, Action work)
        {
            _busy = what;
            PushState();
            try { work(); }
            finally
            {
                _busy = null;
                PushState();
            }
        }

        private void Rescan()
        {
            if (_manifest.Count == 0) return;
            _scanning = true;
            PushState();
            try
            {
                var env = WindowsEnvironment.Build(_config);
                _scanner = new GameScanner(env, _config);
                if (_engine == null)
                {
                    _engine = new SyncEngine(_config, () => new HttpStore(_serverUrl ?? "http://unconfigured"), _scanner, _state, _processes.IsRunning);
                    _engine.ConflictFound += (game, c) => Balloon($"{game}: choose a save",
                        $"It was played here and on {c.RemoteMachine}. Click to choose which save to keep.", ToolTipIcon.Warning, game);
                    _engine.Changed += PushState;
                }
                else
                {
                    _engine.ReplaceScanner(_scanner);
                }
                var scan = _scanner.ScanAll(_manifest, _cts.Token);
                _engine.UpdateLocal(_manifest, scan);
                _processes.SetInstallFolders(scan.InstallPaths);
                foreach (var s in scan.Skipped) Logger.Info("Skipped " + s);
            }
            finally
            {
                _scanning = false;
                PushState();
            }
        }

        // ---- connection -------------------------------------------------------------------------------------

        private void Discover()
        {
            var ts = TailscaleCli.GetStatus();
            _tsLogin = ts.Login;
            bool tsUp = ts.State == TailscaleState.Running;

            if (!tsUp && string.IsNullOrWhiteSpace(_config.ServerUrl))
            {
                SetConn(ts.State == TailscaleState.NotInstalled ? "tailscaleMissing"
                      : ts.State == TailscaleState.NeedsLogin ? "tailscaleLogin" : "tailscaleOff",
                      ts.Error);
                return;
            }

            SetConn("searching");
            var candidates = ServerLocator.Candidates(tsUp ? ts : null, _config.ServerUrl, _config.LastServerUrl);
            var found = ServerLocator.FindAsync(candidates).GetAwaiter().GetResult();

            if (found == null)
            {
                SetConn(string.IsNullOrEmpty(_config.LastServerUrl) ? "notFound" : "offline");
                if (_connState == "offline" && !_offlineShown)
                {
                    _offlineShown = true;
                    Logger.Warn("Save server not reachable; retrying in the background");
                }
                return;
            }

            _serverUrl = found.Url;
            _login = found.Info.Login;
            _mode = found.Info.Mode;
            if (!found.Info.Authorized)
            {
                SetConn("notAuthorized");
                if (!_notAuthorizedShown)
                {
                    _notAuthorizedShown = true;
                    Logger.Warn($"Server found at {found.Url} but {found.Info.Login ?? "this device"} isn't on its allowlist");
                }
                return;
            }

            _offlineShown = _notAuthorizedShown = false;
            if (_config.LastServerUrl != found.Url)
            {
                _config.LastServerUrl = found.Url;
                _config.Save();
            }
            Logger.Info($"Connected to {found.Url} as {found.Info.Login} ({found.Info.Mode} saves)");
            SetConn("connected");
        }

        private void SetConn(string state, string message = null)
        {
            _connState = state;
            _connMessage = message;
            PushState();
        }

        /// <summary>Full two-way sync. Connection problems flip the state so the worker rediscovers.</summary>
        private void FullSync()
        {
            if (_engine == null || _connState != "connected") return;
            try
            {
                var results = _engine.SyncAll(ct: _cts.Token);
                _lastSyncUtc = DateTime.UtcNow;
                Summarize(results);
            }
            catch (Exception ex) when (HandleConnectionError(ex)) { }
        }

        private void SyncOne(string game)
        {
            if (_engine == null) return;
            if (_connState != "connected") { Logger.Info($"{game} closed while offline; it uploads once the server is back"); return; }
            try
            {
                var r = _engine.SyncGame(game);
                Logger.Info(r.ToString());
                if (r.Action == SyncAction.Uploaded) _lastSyncUtc = DateTime.UtcNow;
            }
            catch (Exception ex) when (HandleConnectionError(ex)) { }
        }

        private bool HandleConnectionError(Exception ex)
        {
            if (ex is OperationCanceledException) return true;
            if (ex is NotAuthorizedException na)
            {
                _login = na.Login ?? _login;
                SetConn("notAuthorized");
                return true;
            }
            if (ex is IOException || ex is HttpRequestException || ex is TaskCanceledException || ex is AggregateException)
            {
                Logger.Warn($"Lost the save server: {ex.Message}");
                SetConn("offline");
                _kick.Set();
                return true;
            }
            return false;
        }

        private void Summarize(List<GameSyncResult> results)
        {
            foreach (var r in results.Where(r => r.Action != SyncAction.InSync)) Logger.Info(r.ToString());
            var down = results.Where(r => r.Action == SyncAction.Downloaded).Select(r => r.Game).ToList();
            if (down.Count > 0) Balloon("Newer saves restored", Short(down), ToolTipIcon.Info);
            SetTrayText(results.Any(r => r.Action == SyncAction.Conflict) ? "SaveCloud – a game needs your choice" : "SaveCloud – up to date");
        }

        private static string Short(List<string> names)
            => names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + $" and {names.Count - 3} more";

        // ---- UI state ------------------------------------------------------------------------------------------

        private int _pushPending;

        /// <summary>Sends the current state to the window (coalesced; callable from any thread).</summary>
        private void PushState()
        {
            if (_window == null || Interlocked.Exchange(ref _pushPending, 1) == 1) return;
            Task.Delay(120).ContinueWith(_ =>
            {
                Interlocked.Exchange(ref _pushPending, 0);
                try
                {
                    string json = JsonConvert.SerializeObject(new { type = "state", data = BuildState() });
                    _window?.Post(json);
                }
                catch (Exception ex) { Logger.Error("Could not build UI state", ex); }
            });
        }

        private object BuildState()
        {
            var games = (_engine?.Snapshot() ?? new List<SyncEngine.GameView>())
                .Where(g => !(_config.IgnoredGames ?? new List<string>()).Contains(g.Name))
                .Select(ToCard)
                .ToList();
            return new
            {
                version = typeof(TrayApp).Assembly.GetName().Version.ToString(3),
                machine = Environment.MachineName,
                everConnected = !string.IsNullOrEmpty(_config.LastServerUrl),
                busy = _busy,
                scanning = _scanning || _manifest.Count == 0,
                lastSyncUtc = _lastSyncUtc,
                connection = new { state = _connState, login = _login, tsLogin = _tsLogin, server = _serverUrl, mode = _mode, message = _connMessage },
                settings = new
                {
                    _config.StartWithWindows,
                    _config.SkipSteamCloudGames,
                    _config.IncludeConfigFiles,
                    _config.HistoryToKeep,
                    _config.ServerUrl,
                    _config.ExtraRoots,
                    _config.IgnoredGames,
                },
                games,
            };
        }

        private object ToCard(SyncEngine.GameView g)
        {
            var last = g.LastResult;
            string kind, message = last?.Message;
            if (g.State?.Conflict != null) kind = "conflict";
            else if (_processes.IsRunning(g.Name)) kind = "running";
            else if (last?.Action == SyncAction.Error) kind = "error";
            else if (last?.Action == SyncAction.NotInstalled) kind = "notInstalled";
            else if (last?.Action == SyncAction.LocalMissing) kind = "missing";
            else if (last?.Action == SyncAction.Deferred) kind = "waiting";
            else if (g.ServerRevision == null && g.LocalFiles > 0) { kind = "pending"; message = "Uploads at the next sync"; }
            else if (g.State?.LastSyncUtc != null) kind = "synced";
            else { kind = "waiting"; message = g.ServerRevision != null ? "Downloads at the next sync" : message; }

            var c = g.State?.Conflict;
            var activity = new[] { g.ServerUploadedUtc, g.State?.LastSyncUtc }.Where(d => d != null).DefaultIfEmpty(null).Max();
            return new
            {
                name = g.Name,
                steamId = g.SteamId,
                kind,
                message,
                localFiles = g.LocalFiles,
                localBytes = g.LocalBytes,
                serverRev = g.ServerRevision,
                serverFrom = g.ServerFrom,
                serverLogin = g.ServerLogin,
                serverUtc = g.ServerUploadedUtc,
                activityUtc = activity,
                conflict = c == null ? null : new { localUtc = c.LocalNewestUtc, remoteUtc = c.RemoteNewestUtc, remoteMachine = c.RemoteMachine },
            };
        }

        // ---- commands from the page ---------------------------------------------------------------------------

        private void OnCommand(JObject msg)
        {
            string cmd = (string)msg["cmd"];
            string game = (string)msg["game"];
            switch (cmd)
            {
                case "ready": PushState(); break;
                case "syncNow": Enqueue("Syncing", () => { if (_connState != "connected") Discover(); FullSync(); }); break;
                case "syncGame": Enqueue($"Syncing {game}", () => SyncOne(game)); break;
                case "retry": Enqueue("Connecting", () => { Discover(); if (_connState == "connected") FullSync(); }); break;
                case "rescan": Enqueue("Looking for games", () => { Rescan(); FullSync(); }); break;
                case "resolve":
                    bool keepLocal = (bool)msg["keepLocal"];
                    Enqueue(keepLocal ? $"Uploading {game}" : $"Restoring {game}", () =>
                    {
                        try
                        {
                            var r = _engine.Resolve(game, keepLocal);
                            Logger.Info("Resolved: " + r);
                            Toast(r.Action == SyncAction.Uploaded ? $"{game}: this PC's save is now the newest"
                                : r.Action == SyncAction.Downloaded ? $"{game}: restored (old files backed up)"
                                : $"{game}: {r.Message}");
                        }
                        catch (Exception ex) when (HandleConnectionError(ex)) { }
                    });
                    break;
                case "ignore":
                    if (!_config.IgnoredGames.Contains(game)) _config.IgnoredGames.Add(game);
                    _config.Save();
                    Enqueue("Updating", () => Rescan());
                    break;
                case "unignore":
                    _config.IgnoredGames.Remove(game);
                    _config.Save();
                    Enqueue("Looking for games", () => { Rescan(); FullSync(); });
                    break;
                case "addRoot": AddRoot(); break;
                case "removeRoot":
                    _config.ExtraRoots.Remove((string)msg["path"]);
                    _config.Save();
                    Enqueue("Looking for games", () => Rescan());
                    break;
                case "setSetting": SetSetting((string)msg["key"], msg["value"]); break;
                case "openFolder": OpenFolder(game); break;
                case "openBackups": Explore(AppPaths.BackupDir); break;
                case "openLogs": Explore(AppPaths.LogDir); break;
                case "installTailscale": MainWindow.OpenUrl(TailscaleCli.DownloadUrl); break;
                case "openTailscale": TailscaleCli.OpenApp(); break;
                case "copy": Clipboard.SetText((string)msg["text"] ?? ""); break;
            }
            PushState();
        }

        private void SetSetting(string key, JToken value)
        {
            switch (key)
            {
                case "StartWithWindows":
                    _config.StartWithWindows = (bool)value;
                    StartupRegistration.Apply(_config.StartWithWindows);
                    break;
                case "SkipSteamCloudGames":
                    _config.SkipSteamCloudGames = (bool)value;
                    Enqueue("Looking for games", () => { Rescan(); FullSync(); });
                    break;
                case "IncludeConfigFiles":
                    _config.IncludeConfigFiles = (bool)value;
                    Enqueue("Looking for games", () => { Rescan(); FullSync(); });
                    break;
                case "HistoryToKeep":
                    _config.HistoryToKeep = Math.Max(1, Math.Min(50, (int)value));
                    break;
                case "ServerUrl":
                    _config.ServerUrl = ((string)value ?? "").Trim();
                    Enqueue("Connecting", () => { SetConn("searching"); Discover(); if (_connState == "connected") FullSync(); });
                    break;
                default:
                    return;
            }
            _config.Save();
        }

        private void AddRoot()
        {
            using (var d = new FolderBrowserDialog { Description = "Choose a folder that contains installed games", ShowNewFolderButton = false })
            {
                if (d.ShowDialog(_window) != DialogResult.OK) return;
                if (!_config.ExtraRoots.Contains(d.SelectedPath, StringComparer.OrdinalIgnoreCase)) _config.ExtraRoots.Add(d.SelectedPath);
                _config.Save();
                Enqueue("Looking for games", () => { Rescan(); FullSync(); });
            }
        }

        private void OpenFolder(string game)
        {
            var file = _engine?.Snapshot().FirstOrDefault(g => g.Name == game)?.SampleFile;
            if (file != null && File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\"");
        }

        private static void Explore(string dir)
        {
            try { Process.Start("explorer.exe", $"\"{dir}\""); } catch (Exception ex) { Logger.Warn(ex.Message); }
        }

        // ---- window / tray -------------------------------------------------------------------------------------

        public void ShowWindow()
        {
            OnUi(() =>
            {
                if (_window == null || _window.IsDisposed)
                    _window = new MainWindow(msg => OnUi(() => OnCommand(msg)));
                _window.ShowAndActivate();
                PushState();
            });
        }

        private void Toast(string text)
            => _window?.Post(JsonConvert.SerializeObject(new { type = "toast", text }));

        private void Balloon(string title, string text, ToolTipIcon icon, string game = null)
        {
            OnUi(() =>
            {
                if (_window != null && _window.Visible && _window.ContainsFocus) { Toast(title); return; }
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = string.IsNullOrEmpty(text) ? " " : text;
                _tray.BalloonTipIcon = icon;
                _tray.ShowBalloonTip(5000);
            });
        }

        private void SetTrayText(string text) => OnUi(() => _tray.Text = text.Length > 63 ? text.Substring(0, 63) : text);

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
            _processes?.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _window?.Dispose();
            _ui.Dispose();
            base.ExitThreadCore();
        }
    }
}
