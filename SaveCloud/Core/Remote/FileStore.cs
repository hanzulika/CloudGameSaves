using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SaveCloud.Core.Remote
{
    /// <summary>
    /// Save storage on a plain file transport (a local/network folder; used by the tests):
    /// <code>
    /// &lt;root&gt;/games/&lt;slug&gt;/head.json        current revision (replaced atomically)
    /// &lt;root&gt;/games/&lt;slug&gt;/rev-000042.zip    one zip per revision, entries named by template
    /// &lt;root&gt;/games/&lt;slug&gt;/.lock/          exists while a PC is writing (mkdir is atomic)
    /// </code>
    /// </summary>
    public class FileStore : IRemoteStore
    {
        public static readonly TimeSpan StaleLockAge = TimeSpan.FromMinutes(10);
        private readonly IRemoteTransport _t;

        public FileStore(IRemoteTransport transport) { _t = transport; }

        public IRemoteTransport Transport => _t;

        public void Connect()
        {
            _t.Connect();
            _t.EnsureDirectory("games");
        }

        private static string Dir(string game) => "games/" + Slugs.Of(game);

        public List<RemoteHead> ReadAllHeads()
        {
            var heads = new List<RemoteHead>();
            foreach (var d in _t.List("games").Where(e => e.IsDirectory))
            {
                try
                {
                    var head = ReadJson<RemoteHead>("games/" + d.Name + "/head.json");
                    if (head?.Game != null && head.Format == 1) heads.Add(head);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Skipping unreadable server entry {d.Name}: {ex.Message}");
                }
            }
            return heads;
        }

        public RemoteHead ReadHead(string game) => ReadJson<RemoteHead>(Dir(game) + "/head.json");

        private T ReadJson<T>(string path) where T : class
        {
            if (!_t.Exists(path)) return null;
            using (var ms = new MemoryStream())
            {
                _t.Read(path, ms);
                return JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(ms.ToArray()));
            }
        }

        /// <summary>Takes the per-game write lock. Breaks locks left behind by a PC that crashed mid-upload.</summary>
        public IDisposable Lock(string game, TimeSpan wait)
        {
            string dir = Dir(game);
            string lockDir = dir + "/.lock";
            _t.EnsureDirectory(dir);
            var deadline = DateTime.UtcNow + wait;

            while (true)
            {
                if (_t.TryCreateDirectoryExclusive(lockDir))
                {
                    var owner = Encoding.UTF8.GetBytes($"{Environment.MachineName} {DateTime.UtcNow:o}");
                    using (var ms = new MemoryStream(owner)) _t.Write(lockDir + "/owner", ms);
                    return new LockHandle(_t, lockDir);
                }

                var st = _t.Stat(lockDir);
                if (st != null && DateTime.UtcNow - st.ModifiedUtc > StaleLockAge)
                {
                    Logger.Warn($"Breaking stale server lock for {game} (from {st.ModifiedUtc:u})");
                    try { _t.DeleteDirectory(lockDir); } catch { }
                    continue;
                }

                if (DateTime.UtcNow > deadline) throw new LockTimeoutException(game);
                Thread.Sleep(1000);
            }
        }

        private class LockHandle : IDisposable
        {
            private readonly IRemoteTransport _t;
            private readonly string _dir;
            private bool _done;
            public LockHandle(IRemoteTransport t, string dir) { _t = t; _dir = dir; }
            public void Dispose()
            {
                if (_done) return;
                _done = true;
                try { _t.DeleteDirectory(_dir); }
                catch (Exception ex) { Logger.Warn($"Could not release server lock {_dir}: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Publishes a new revision under the per-game lock. Throws <see cref="RevisionConflictException"/> when the
        /// server moved past <paramref name="expectedRevision"/> (another PC uploaded first).
        /// </summary>
        public RemoteHead Publish(string game, long expectedRevision, string archivePath, string archiveSha256,
                                  string fingerprint, List<RemoteFileEntry> files, int historyToKeep)
        {
            using (Lock(game, TimeSpan.FromSeconds(60)))
                return PublishLocked(game, expectedRevision, archivePath, archiveSha256, fingerprint, files, historyToKeep);
        }

        private RemoteHead PublishLocked(string game, long expectedRevision, string archivePath, string archiveSha256,
                                         string fingerprint, List<RemoteFileEntry> files, int historyToKeep)
        {
            string dir = Dir(game);
            var current = ReadHead(game);
            long currentRev = current?.Revision ?? 0;
            if (currentRev != expectedRevision) throw new RevisionConflictException(current);

            long rev = currentRev + 1;
            string archive = $"rev-{rev:D6}.zip";
            using (var fs = File.OpenRead(archivePath))
                _t.Write(dir + "/." + archive + ".part", fs);
            _t.RenameReplace(dir + "/." + archive + ".part", dir + "/" + archive);

            var head = new RemoteHead
            {
                Game = game,
                Revision = rev,
                Fingerprint = fingerprint,
                Archive = archive,
                ArchiveSha256 = archiveSha256,
                ArchiveSize = new FileInfo(archivePath).Length,
                UploadedUtc = DateTime.UtcNow,
                UploadedBy = Environment.MachineName,
                Files = files,
            };
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(head, Formatting.Indented))))
                _t.Write(dir + "/head.json.part", ms);
            _t.RenameReplace(dir + "/head.json.part", dir + "/head.json");

            Prune(dir, archive, historyToKeep);
            return head;
        }

        private void Prune(string dir, string currentArchive, int keep)
        {
            try
            {
                var old = _t.List(dir)
                    .Where(e => !e.IsDirectory && e.Name.StartsWith("rev-") && e.Name.EndsWith(".zip") && e.Name != currentArchive)
                    .OrderByDescending(e => e.Name, StringComparer.Ordinal)
                    .Skip(Math.Max(0, keep - 1))
                    .ToList();
                foreach (var e in old) _t.Delete(dir + "/" + e.Name);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not prune old revisions in {dir}: {ex.Message}");
            }
        }

        /// <summary>Downloads the archive of <paramref name="head"/> and verifies its checksum.</summary>
        public void DownloadArchive(RemoteHead head, string localPath)
        {
            using (var fs = File.Create(localPath))
                _t.Read(Dir(head.Game) + "/" + head.Archive, fs);
            string sha = Hashing.FileSha256(localPath);
            if (!string.Equals(sha, head.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Download of {head.Game} revision {head.Revision} is corrupt (checksum mismatch)");
        }

        public void Dispose() => _t.Dispose();
    }

}
