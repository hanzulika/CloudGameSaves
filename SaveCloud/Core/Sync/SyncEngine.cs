using SaveCloud.Core.Manifest;
using SaveCloud.Core.Remote;
using SaveCloud.Core.Scan;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace SaveCloud.Core.Sync
{
    public enum SyncAction
    {
        InSync,
        Uploaded,
        Downloaded,
        /// <summary>Both sides changed; waiting for the user to pick one.</summary>
        Conflict,
        /// <summary>The game is running; retried after it closes.</summary>
        Deferred,
        /// <summary>Server copy needs the game's install folder, and the game isn't installed here.</summary>
        NotInstalled,
        /// <summary>Local saves disappeared; the server copy is kept and not deleted.</summary>
        LocalMissing,
        PendingUpload,
        PendingDownload,
        Error,
    }

    public class GameSyncResult
    {
        public string Game { get; set; }
        public SyncAction Action { get; set; }
        public string Message { get; set; }
        public override string ToString() => $"{Game}: {Action}{(string.IsNullOrEmpty(Message) ? "" : " - " + Message)}";
    }

    /// <summary>
    /// Decides, per game, whether to upload, download, or stop and ask. The rule:
    /// <list type="bullet">
    /// <item>only this PC changed since the last sync: upload a new revision</item>
    /// <item>only the server changed: download it (after backing up the local files)</item>
    /// <item>both changed: conflict; nothing is overwritten until the user chooses</item>
    /// </list>
    /// "Changed" is decided by content fingerprints and server revision numbers, never by clocks.
    /// </summary>
    public class SyncEngine
    {
        private readonly AppConfig _config;
        private readonly Func<IRemoteStore> _storeFactory;
        private GameScanner _scanner;
        private readonly SyncState _state;
        private readonly Func<string, bool> _isRunning;
        /// <summary>Serializes sync operations (one network conversation at a time).</summary>
        private readonly object _gate = new object();
        /// <summary>Guards the in-memory view so the UI can read it while a sync is running.</summary>
        private readonly object _view = new object();

        private Dictionary<string, ManifestGame> _manifest = new Dictionary<string, ManifestGame>(StringComparer.Ordinal);
        private ScanResult _scan = new ScanResult();
        private readonly Dictionary<string, RemoteHead> _heads = new Dictionary<string, RemoteHead>(StringComparer.Ordinal);
        private readonly Dictionary<string, GameSyncResult> _last = new Dictionary<string, GameSyncResult>(StringComparer.Ordinal);

        /// <summary>Raised after any game's status changes (from a background thread).</summary>
        public event Action Changed;
        /// <summary>Raised when a new conflict needs the user's decision.</summary>
        public event Action<string, ConflictInfo> ConflictFound;

        public SyncEngine(AppConfig config, Func<IRemoteStore> storeFactory, GameScanner scanner,
                          SyncState state, Func<string, bool> isRunning)
        {
            _config = config;
            _storeFactory = storeFactory;
            _scanner = scanner;
            _state = state;
            _isRunning = isRunning ?? (_ => false);
        }

        public SyncState State => _state;

        /// <summary>Swaps in a scanner built from new settings (game folders, config-file option).</summary>
        public void ReplaceScanner(GameScanner scanner)
        {
            lock (_gate)
            {
                _scanner = scanner;
                _hashCache.Clear();
            }
        }

        /// <summary>Gives the engine the latest manifest and full scan.</summary>
        public void UpdateLocal(IList<ManifestGame> manifest, ScanResult scan)
        {
            lock (_view)
            {
                _manifest = manifest.GroupBy(g => g.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                _scan = scan;
            }
        }

        public ScanResult LastScan { get { lock (_view) return _scan; } }

        public class GameView
        {
            public string Name { get; set; }
            public int LocalFiles { get; set; }
            public long LocalBytes { get; set; }
            public bool Installed { get; set; }
            public long? ServerRevision { get; set; }
            public string ServerFrom { get; set; }
            public string ServerLogin { get; set; }
            public DateTime? ServerUploadedUtc { get; set; }
            public long? SteamId { get; set; }
            /// <summary>A local save file, for "open save folder".</summary>
            public string SampleFile { get; set; }
            public GameState State { get; set; }
            public GameSyncResult LastResult { get; set; }
        }

        /// <summary>Snapshot of every known game for the status window.</summary>
        public List<GameView> Snapshot()
        {
            lock (_view)
            {
                var names = new HashSet<string>(_scan.Games.Select(g => g.Name), StringComparer.Ordinal);
                names.UnionWith(_heads.Keys);
                names.UnionWith(_state.Games.Keys);
                return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n =>
                {
                    var local = _scan.Games.FirstOrDefault(g => g.Name == n);
                    _heads.TryGetValue(n, out var head);
                    _last.TryGetValue(n, out var last);
                    return new GameView
                    {
                        Name = n,
                        LocalFiles = local?.Files.Count ?? 0,
                        LocalBytes = local?.TotalBytes ?? 0,
                        Installed = _scan.InstallPaths.ContainsKey(n),
                        ServerRevision = head?.Revision,
                        ServerFrom = head?.UploadedBy,
                        ServerLogin = head?.UploadedLogin,
                        ServerUploadedUtc = head?.UploadedUtc,
                        SteamId = local?.Manifest.SteamId ?? (_manifest.TryGetValue(n, out var mg) ? mg.SteamId : null),
                        SampleFile = local?.Files.FirstOrDefault()?.ActualPath,
                        State = _state.Get(n),
                        LastResult = last,
                    };
                }).ToList();
            }
        }

        // ---- entry points ------------------------------------------------------------------------------

        /// <summary>
        /// Offline check (no server connection): games whose local saves differ from the last synced version and
        /// haven't been written to for <paramref name="quietFor"/>, so a save that's still being written isn't uploaded.
        /// </summary>
        public List<string> FindLocalChanges(TimeSpan quietFor)
        {
            lock (_gate)
            {
                var changed = new List<string>();
                foreach (var g in LastScan.Games.Select(x => x.Name).ToList())
                {
                    try
                    {
                        if (_isRunning(g)) continue;
                        var gs = _state.Get(g);
                        if (gs?.Conflict != null) continue;
                        var (mg, basePaths) = Locate(g, null);
                        var local = _scanner.ScanGame(mg, basePaths);
                        if (local == null) continue;
                        if (DateTime.UtcNow - local.Files.Max(f => f.ModifiedUtc) < quietFor) continue;
                        if (LocalFingerprint(local.Files) != gs?.BaseFingerprint) changed.Add(g);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Could not check {g} for changes: {ex.Message}");
                    }
                }
                return changed;
            }
        }

        /// <summary>Syncs every game known locally or on the server. Used at startup and on the periodic timer.</summary>
        public List<GameSyncResult> SyncAll(bool allowPush = true, bool allowPull = true, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var results = new List<GameSyncResult>();
                using (var store = Open())
                {
                    var all = store.ReadAllHeads();
                    var heads = new Dictionary<string, RemoteHead>(StringComparer.Ordinal);
                    foreach (var h in all) heads[h.Game] = h;
                    lock (_view)
                    {
                        _heads.Clear();
                        foreach (var kv in heads) _heads[kv.Key] = kv.Value;
                    }

                    var names = new HashSet<string>(heads.Keys, StringComparer.Ordinal);
                    names.UnionWith(LastScan.Games.Select(g => g.Name));
                    var ignored = new HashSet<string>(_config.IgnoredGames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

                    foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (ignored.Contains(name)) continue;
                        heads.TryGetValue(name, out var head);
                        results.Add(Record(SafeSyncOne(store, name, head, allowPush, allowPull)));
                    }
                }
                Changed?.Invoke();
                return results;
            }
        }

        /// <summary>Syncs one game, e.g. right after it closed.</summary>
        public GameSyncResult SyncGame(string name, bool allowPush = true, bool allowPull = true)
        {
            lock (_gate)
            {
                GameSyncResult r;
                using (var store = Open())
                {
                    var head = store.ReadHead(name);
                    SetHead(name, head);
                    r = Record(SafeSyncOne(store, name, head, allowPush, allowPull));
                }
                Changed?.Invoke();
                return r;
            }
        }

        /// <summary>Settles a conflict. keepLocal = upload this PC's saves over the server; otherwise restore the server's.</summary>
        public GameSyncResult Resolve(string name, bool keepLocal)
        {
            lock (_gate)
            {
                GameSyncResult r;
                using (var store = Open())
                {
                    try
                    {
                        if (_isRunning(name)) return Record(Result(name, SyncAction.Deferred, "Close the game first"));
                        var head = store.ReadHead(name);
                        SetHead(name, head);
                        var (mg, basePaths) = Locate(name, head);
                        var local = _scanner.ScanGame(mg, basePaths);

                        if (keepLocal)
                        {
                            if (local == null) return Record(Result(name, SyncAction.LocalMissing, "No local saves to keep"));
                            r = Push(store, name, local, head?.Revision ?? 0);
                        }
                        else
                        {
                            if (head == null) return Record(Result(name, SyncAction.Error, "Server has no copy"));
                            r = Pull(store, name, mg, basePaths, local, head);
                        }
                    }
                    catch (Exception ex)
                    {
                        r = Error(name, ex);
                    }
                    r = Record(r);
                }
                Changed?.Invoke();
                return r;
            }
        }

        // ---- core decision -----------------------------------------------------------------------------

        private IRemoteStore Open()
        {
            var store = _storeFactory();
            try
            {
                store.Connect();
                return store;
            }
            catch
            {
                store.Dispose();
                throw;
            }
        }

        private GameSyncResult SafeSyncOne(IRemoteStore store, string name, RemoteHead head, bool allowPush, bool allowPull)
        {
            try { return SyncOne(store, name, head, allowPush, allowPull); }
            catch (Exception ex) { return Error(name, ex); }
        }

        private GameSyncResult SyncOne(IRemoteStore store, string name, RemoteHead head, bool allowPush, bool allowPull)
        {
            var (mg, basePaths) = Locate(name, head);
            var local = _scanner.ScanGame(mg, basePaths);
            string L = local == null ? null : LocalFingerprint(local.Files);
            var gs = _state.Get(name);

            // Identical content on both sides: nothing to move, just record the agreement.
            if (head != null && L == head.Fingerprint)
            {
                if (gs == null || gs.BaseRevision != head.Revision || gs.BaseFingerprint != L || gs.Conflict != null)
                    Agree(name, head.Revision, L, gs?.Conflict != null ? "Conflict cleared (files now identical)" : "Already identical");
                return Result(name, SyncAction.InSync);
            }

            if (gs?.Conflict != null)
                return Result(name, SyncAction.Conflict, "Waiting for you to choose which copy to keep");

            if (head == null && L == null) return Result(name, SyncAction.InSync);

            bool localChanged = gs == null ? L != null : L != gs.BaseFingerprint;
            bool remoteChanged = head != null && (gs == null || head.Revision != gs.BaseRevision);
            if (!localChanged && !remoteChanged) return Result(name, SyncAction.InSync);

            if (_isRunning(name)) return Result(name, SyncAction.Deferred, "Game is running");

            if (localChanged && !remoteChanged)
            {
                if (L == null) return Result(name, SyncAction.LocalMissing, "Local saves are gone; keeping the server copy");
                if (!allowPush) return Result(name, SyncAction.PendingUpload);
                return Push(store, name, local, gs?.BaseRevision ?? 0);
            }

            if (!localChanged || L == null)
            {
                if (!allowPull) return Result(name, SyncAction.PendingDownload);
                return Pull(store, name, mg, basePaths, local, head);
            }

            return MarkConflict(name, local, head);
        }

        private GameSyncResult Push(IRemoteStore store, string name, DetectedGame local, long expectedRevision)
        {
            string zip = Path.Combine(AppPaths.TempDir, Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                var built = SaveArchive.Create(local.Files, zip);
                RemoteHead head;
                try
                {
                    head = store.Publish(name, expectedRevision, zip, built.Sha256, built.Fingerprint, built.Files, _config.HistoryToKeep);
                }
                catch (RevisionConflictException ex)
                {
                    SetHead(name, ex.Current);
                    if (ex.Current?.Fingerprint == built.Fingerprint)
                    {
                        Agree(name, ex.Current.Revision, built.Fingerprint, "Already identical");
                        return Result(name, SyncAction.InSync);
                    }
                    // Another PC uploaded between our check and our write.
                    return MarkConflict(name, local, ex.Current);
                }

                SetHead(name, head);
                Agree(name, head.Revision, built.Fingerprint, $"Uploaded revision {head.Revision}");
                Logger.Info($"Uploaded {name}: revision {head.Revision}, {built.Files.Count} files");
                return Result(name, SyncAction.Uploaded, $"Revision {head.Revision}");
            }
            finally
            {
                TryDelete(zip);
            }
        }

        private GameSyncResult Pull(IRemoteStore store, string name, ManifestGame mg, List<string> basePaths,
                                    DetectedGame local, RemoteHead head)
        {
            // Check placement before downloading anything.
            foreach (var f in head.Files)
                if (_scanner.Paths.Resolve(f.Path, basePaths) == null)
                    return Result(name, SyncAction.NotInstalled, "Install the game on this PC to restore its saves");

            string zip = Path.Combine(AppPaths.TempDir, Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                store.DownloadArchive(head, zip);
                string backup = SaveArchive.Backup(name, local?.Files);
                if (backup != null) Logger.Info($"Backed up local {name} saves to {backup}");

                var written = SaveArchive.Restore(zip, head, t => _scanner.Paths.Resolve(t, basePaths),
                                                  local?.Files, StringComparer.OrdinalIgnoreCase);
                if (written == null)
                    return Result(name, SyncAction.NotInstalled, "Install the game on this PC to restore its saves");

                var after = _scanner.ScanGame(mg, basePaths);
                string L2 = after == null ? null : LocalFingerprint(after.Files);
                if (L2 != head.Fingerprint)
                    Logger.Warn($"{name}: restored files differ from the server copy after rescan (another rule matches extra files?)");

                Agree(name, head.Revision, L2, $"Downloaded revision {head.Revision} from {head.UploadedBy}");
                Logger.Info($"Downloaded {name}: revision {head.Revision} from {head.UploadedBy}, {written.Count} files");
                return Result(name, SyncAction.Downloaded, $"Revision {head.Revision} from {head.UploadedBy}");
            }
            finally
            {
                TryDelete(zip);
            }
        }

        private GameSyncResult MarkConflict(string name, DetectedGame local, RemoteHead head)
        {
            var gs = _state.Get(name) ?? new GameState();
            bool isNew = gs.Conflict == null || gs.Conflict.RemoteRevision != head.Revision;
            gs.Conflict = new ConflictInfo
            {
                DetectedUtc = DateTime.UtcNow,
                RemoteRevision = head.Revision,
                RemoteMachine = head.UploadedBy,
                RemoteNewestUtc = head.NewestFileUtc,
                LocalNewestUtc = local?.Files.Count > 0 ? local.Files.Max(f => f.ModifiedUtc) : DateTime.MinValue,
            };
            gs.LastAction = "Conflict";
            _state.Set(name, gs);
            if (isNew)
            {
                Logger.Warn($"Conflict on {name}: changed on this PC and on {head.UploadedBy} (revision {head.Revision})");
                try { ConflictFound?.Invoke(name, gs.Conflict); } catch { }
            }
            return Result(name, SyncAction.Conflict, $"Changed here and on {head.UploadedBy}");
        }

        private void Agree(string name, long revision, string fingerprint, string action)
        {
            _state.Set(name, new GameState
            {
                BaseRevision = revision,
                BaseFingerprint = fingerprint,
                LastSyncUtc = DateTime.UtcNow,
                LastAction = action,
                Conflict = null,
            });
        }

        // ---- helpers -----------------------------------------------------------------------------------

        /// <summary>Finds the manifest entry and install folders for a game. Games missing from the manifest (renamed
        /// upstream, or added by another PC with a newer manifest) fall back to the paths recorded on the server.</summary>
        private (ManifestGame, List<string>) Locate(string name, RemoteHead head)
        {
            Dictionary<string, ManifestGame> manifest;
            lock (_view) manifest = _manifest;
            if (!manifest.TryGetValue(name, out var mg))
            {
                mg = new ManifestGame
                {
                    Name = name,
                    Files = (head?.Files ?? new List<RemoteFileEntry>())
                        .Select(f => new FileRule { Pattern = f.Path, IsSave = true })
                        .ToList(),
                };
            }
            LastScan.InstallPaths.TryGetValue(name, out var basePaths);
            return (mg, basePaths ?? new List<string>());
        }

        private readonly Dictionary<string, (long size, DateTime mtime, string sha)> _hashCache =
            new Dictionary<string, (long, DateTime, string)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Content fingerprint of local files; unchanged files (same size and time) aren't re-hashed.</summary>
        private string LocalFingerprint(List<SaveFile> files)
        {
            var pairs = new List<(string, string)>();
            foreach (var f in files)
            {
                var fi = new FileInfo(f.ActualPath);
                if (!fi.Exists) continue;
                if (!_hashCache.TryGetValue(f.ActualPath, out var c) || c.size != fi.Length || c.mtime != fi.LastWriteTimeUtc)
                {
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                    using (var fs = new FileStream(f.ActualPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        c = (fi.Length, fi.LastWriteTimeUtc, Hashing.Hex(sha.ComputeHash(fs)));
                    _hashCache[f.ActualPath] = c;
                }
                pairs.Add((f.Template, c.sha));
            }
            return pairs.Count == 0 ? null : Hashing.Fingerprint(pairs);
        }

        private void SetHead(string name, RemoteHead head)
        {
            if (head == null) return;
            lock (_view) _heads[name] = head;
        }

        private GameSyncResult Record(GameSyncResult r)
        {
            lock (_view) _last[r.Game] = r;
            return r;
        }

        private static GameSyncResult Result(string game, SyncAction a, string msg = null)
            => new GameSyncResult { Game = game, Action = a, Message = msg };

        private static GameSyncResult Error(string game, Exception ex)
        {
            if (!(ex is LockTimeoutException)) Logger.Error($"Sync of {game} failed", ex);
            return Result(game, ex is LockTimeoutException ? SyncAction.Deferred : SyncAction.Error, ex.Message);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
