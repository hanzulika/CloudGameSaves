using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SaveCloud.Core.Remote
{
    public class RemoteFileEntry
    {
        /// <summary>Portable template path, e.g. "&lt;winAppData&gt;/Game/slot1.sav".</summary>
        public string Path { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
        public DateTime ModifiedUtc { get; set; }
    }

    /// <summary>The current revision of one game's saves on the server (games/&lt;slug&gt;/head.json).</summary>
    public class RemoteHead
    {
        public int Format { get; set; } = 1;
        public string Game { get; set; }
        public long Revision { get; set; }
        /// <summary>Hash over all file paths and contents; equal fingerprints mean identical saves.</summary>
        public string Fingerprint { get; set; }
        public string Archive { get; set; }
        public string ArchiveSha256 { get; set; }
        public long ArchiveSize { get; set; }
        public DateTime UploadedUtc { get; set; }
        public string UploadedBy { get; set; }
        public List<RemoteFileEntry> Files { get; set; } = new List<RemoteFileEntry>();

        [JsonIgnore]
        public DateTime NewestFileUtc => Files.Count == 0 ? UploadedUtc : Files.Max(f => f.ModifiedUtc);
    }

    public class LockTimeoutException : Exception
    {
        public LockTimeoutException(string game) : base($"Another PC is syncing \"{game}\" right now; will retry.") { }
    }

    public class RevisionConflictException : Exception
    {
        public RemoteHead Current { get; }
        public RevisionConflictException(RemoteHead current)
            : base($"Server already has revision {current?.Revision} from {current?.UploadedBy}") { Current = current; }
    }

    /// <summary>
    /// Server layout and protocol, on top of any transport:
    /// <code>
    /// &lt;root&gt;/games/&lt;slug&gt;/head.json        current revision (replaced atomically)
    /// &lt;root&gt;/games/&lt;slug&gt;/rev-000042.zip    one zip per revision, entries named by template
    /// &lt;root&gt;/games/&lt;slug&gt;/.lock/          exists while a PC is writing (mkdir is atomic)
    /// </code>
    /// </summary>
    public class RemoteStore : IDisposable
    {
        public static readonly TimeSpan StaleLockAge = TimeSpan.FromMinutes(10);
        private readonly IRemoteTransport _t;

        public RemoteStore(IRemoteTransport transport) { _t = transport; }

        public IRemoteTransport Transport => _t;

        public void Connect()
        {
            _t.Connect();
            _t.EnsureDirectory("games");
        }

        public static string Slug(string gameName)
        {
            var sb = new StringBuilder();
            foreach (char c in gameName)
                sb.Append(char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.' || c == '(' || c == ')' ? c : '_');
            string clean = sb.ToString().Trim().TrimEnd('.').Replace(' ', '_');
            if (clean.Length > 80) clean = clean.Substring(0, 80);
            if (clean.Length == 0) clean = "game";
            // Short hash of the exact name keeps slugs unique even when two names clean up the same way.
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(gameName));
                return clean + "-" + BitConverter.ToString(h, 0, 4).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string Dir(string game) => "games/" + Slug(game);

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
        /// Publishes a new revision. Caller must hold the lock. Throws <see cref="RevisionConflictException"/> when the
        /// server moved past <paramref name="expectedRevision"/> (another PC uploaded first).
        /// </summary>
        public RemoteHead Publish(string game, long expectedRevision, string archivePath, string archiveSha256,
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

    public static class Hashing
    {
        public static string FileSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return Hex(sha.ComputeHash(fs));
        }

        public static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        /// <summary>
        /// Order-independent fingerprint of a set of (template, sha256) pairs. Paths are compared case-insensitively,
        /// like Windows does, so "Saves" vs "saves" on two PCs isn't seen as a change.
        /// </summary>
        public static string Fingerprint(IEnumerable<(string template, string sha256)> files)
        {
            var sb = new StringBuilder();
            foreach (var f in files.Select(f => (t: f.template.ToLowerInvariant(), h: f.sha256.ToLowerInvariant()))
                                   .OrderBy(f => f.t, StringComparer.Ordinal))
                sb.Append(f.t).Append('\t').Append(f.h).Append('\n');
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
        }
    }
}
