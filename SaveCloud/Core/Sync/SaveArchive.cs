using SaveCloud.Core.Remote;
using SaveCloud.Core.Scan;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace SaveCloud.Core.Sync
{
    /// <summary>Zips a game's save files with portable template names, and restores them.</summary>
    public static class SaveArchive
    {
        public class Built
        {
            public string Path { get; set; }
            public string Sha256 { get; set; }
            public string Fingerprint { get; set; }
            public List<RemoteFileEntry> Files { get; set; }
        }

        /// <summary>Creates a zip of <paramref name="files"/>. Hashes are computed from the bytes actually archived.</summary>
        public static Built Create(IEnumerable<SaveFile> files, string zipPath)
        {
            var entries = new List<RemoteFileEntry>();
            using (var fs = File.Create(zipPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var f in files)
                {
                    var entry = zip.CreateEntry(f.Template, CompressionLevel.Optimal);
                    var mtime = File.GetLastWriteTimeUtc(f.ActualPath);
                    entry.LastWriteTime = ClampZipTime(mtime);
                    long size;
                    string sha;
                    using (var src = new FileStream(f.ActualPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var dst = entry.Open())
                    using (var hasher = SHA256.Create())
                    using (var cs = new CryptoStream(dst, hasher, CryptoStreamMode.Write))
                    {
                        src.CopyTo(cs);
                        size = src.Length;
                        cs.FlushFinalBlock();
                        sha = Hashing.Hex(hasher.Hash);
                    }
                    entries.Add(new RemoteFileEntry { Path = f.Template, Size = size, Sha256 = sha, ModifiedUtc = mtime });
                }
            }

            return new Built
            {
                Path = zipPath,
                Sha256 = Hashing.FileSha256(zipPath),
                Fingerprint = Hashing.Fingerprint(entries.Select(e => (e.Path, e.Sha256))),
                Files = entries,
            };
        }

        /// <summary>
        /// Restores an archive. Every target is resolved first; if any can't be placed on this PC nothing is written.
        /// Files are staged next to their targets and swapped in at the end to keep the window for a half-restore small.
        /// Local files in <paramref name="currentLocal"/> that aren't in the archive are deleted (they were deleted
        /// on the PC that uploaded). Returns the paths written, or null if the archive can't be placed here.
        /// </summary>
        public static List<string> Restore(string zipPath, RemoteHead head, Func<string, string> resolve,
                                           IEnumerable<SaveFile> currentLocal, StringComparer pathComparer)
        {
            var times = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in head.Files) times[f.Path] = f.ModifiedUtc;
            var staged = new List<(string tmp, string target, DateTime mtime)>();

            using (var fs = File.OpenRead(zipPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                var targets = new List<(ZipArchiveEntry entry, string target)>();
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.EndsWith("/")) continue;
                    string target = resolve(e.FullName);
                    if (target == null) return null;
                    targets.Add((e, target));
                }

                try
                {
                    foreach (var (entry, target) in targets)
                    {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                        string tmp = target + ".savecloud-tmp";
                        using (var src = entry.Open())
                        using (var dst = File.Create(tmp))
                            src.CopyTo(dst);
                        staged.Add((tmp, target, times.TryGetValue(entry.FullName, out var t) ? t : entry.LastWriteTime.UtcDateTime));
                    }
                }
                catch
                {
                    foreach (var s in staged) TryDelete(s.tmp);
                    throw;
                }
            }

            var written = new List<string>();
            foreach (var (tmp, target, mtime) in staged)
            {
                if (File.Exists(target))
                {
                    File.SetAttributes(target, FileAttributes.Normal);
                    File.Delete(target);
                }
                File.Move(tmp, target);
                File.SetLastWriteTimeUtc(target, mtime);
                written.Add(target);
            }

            var keep = new HashSet<string>(written, pathComparer);
            foreach (var f in currentLocal ?? Enumerable.Empty<SaveFile>())
            {
                if (keep.Contains(f.ActualPath)) continue;
                try
                {
                    File.SetAttributes(f.ActualPath, FileAttributes.Normal);
                    File.Delete(f.ActualPath);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Logger.Warn($"Could not remove {f.ActualPath}: {ex.Message}");
                }
            }
            return written;
        }

        /// <summary>Zips the current local files before they're overwritten, so a restore can always be undone.</summary>
        public static string Backup(string game, IList<SaveFile> files, int keepPerGame = 10)
        {
            if (files == null || files.Count == 0) return null;
            string dir = System.IO.Path.Combine(AppPaths.BackupDir, RemoteStore.Slug(game));
            Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            Create(files, path);

            foreach (var old in new DirectoryInfo(dir).GetFiles("*.zip").OrderByDescending(f => f.Name).Skip(keepPerGame))
                TryDelete(old.FullName);
            return path;
        }

        private static DateTimeOffset ClampZipTime(DateTime utc)
        {
            // Zip timestamps can't represent dates before 1980.
            var min = new DateTime(1980, 1, 2, 0, 0, 0, DateTimeKind.Utc);
            return new DateTimeOffset(utc < min ? min : utc);
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }
    }
}
