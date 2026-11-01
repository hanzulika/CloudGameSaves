using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace SaveCloud.Core.Sync
{
    public class ConflictInfo
    {
        public DateTime DetectedUtc { get; set; }
        public long RemoteRevision { get; set; }
        public string RemoteMachine { get; set; }
        /// <summary>Newest file time in the server copy (when that PC last saved).</summary>
        public DateTime RemoteNewestUtc { get; set; }
        /// <summary>Newest file time in this PC's copy.</summary>
        public DateTime LocalNewestUtc { get; set; }
    }

    /// <summary>What this PC last agreed with the server about one game.</summary>
    public class GameState
    {
        /// <summary>Server revision the local files were last identical to (0 = never synced).</summary>
        public long BaseRevision { get; set; }
        /// <summary>Fingerprint of the local files at that point; a different fingerprint now = changed locally.</summary>
        public string BaseFingerprint { get; set; }
        public DateTime? LastSyncUtc { get; set; }
        public string LastAction { get; set; }
        /// <summary>Set when both this PC and the server changed since the last sync. Nothing is overwritten until resolved.</summary>
        public ConflictInfo Conflict { get; set; }
    }

    /// <summary>Per-PC sync bookkeeping in %AppData%\SaveCloud\state.json.</summary>
    public class SyncState
    {
        private readonly object _lock = new object();
        private string _path;

        public Dictionary<string, GameState> Games { get; set; } = new Dictionary<string, GameState>(StringComparer.Ordinal);

        public static SyncState Load(string path = null)
        {
            path = path ?? AppPaths.StateFile;
            SyncState s = null;
            try
            {
                if (File.Exists(path))
                    s = JsonConvert.DeserializeObject<SyncState>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                // Losing state is safe: every game is then re-compared by content, and differences become conflicts.
                Logger.Error("Sync state unreadable; starting fresh", ex);
            }
            s = s ?? new SyncState();
            s.Games = new Dictionary<string, GameState>(s.Games ?? new Dictionary<string, GameState>(), StringComparer.Ordinal);
            s._path = path;
            return s;
        }

        public GameState Get(string game)
        {
            lock (_lock) return Games.TryGetValue(game, out var g) ? g : null;
        }

        public void Set(string game, GameState value)
        {
            lock (_lock)
            {
                Games[game] = value;
                Save();
            }
        }

        public void Save()
        {
            lock (_lock) JsonFile.WriteAtomic(_path ?? AppPaths.StateFile, this);
        }
    }
}
