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

    /// <summary>The current revision of one game's saves on the server.</summary>
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
        /// <summary>PC name that uploaded this revision.</summary>
        public string UploadedBy { get; set; }
        /// <summary>Tailscale login that uploaded it (server-set; empty for folder storage).</summary>
        public string UploadedLogin { get; set; }
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

    /// <summary>Where saves are stored: the SaveCloud server (HTTP over Tailscale) or a folder (tests).</summary>
    public interface IRemoteStore : IDisposable
    {
        void Connect();
        List<RemoteHead> ReadAllHeads();
        RemoteHead ReadHead(string game);
        /// <summary>
        /// Adds revision <paramref name="expectedRevision"/>+1. Throws <see cref="RevisionConflictException"/> if
        /// the server is no longer at <paramref name="expectedRevision"/> (another PC uploaded first).
        /// </summary>
        RemoteHead Publish(string game, long expectedRevision, string archivePath, string archiveSha256,
                           string fingerprint, List<RemoteFileEntry> files, int historyToKeep);
        /// <summary>Downloads the archive of <paramref name="head"/> and verifies its checksum.</summary>
        void DownloadArchive(RemoteHead head, string localPath);
    }

    public static class Slugs
    {
        /// <summary>
        /// Stable, server-safe id for a game name: ASCII letters, digits and _-.() only, never starting with '.',
        /// plus a short hash of the exact name so two names that clean up the same way stay distinct.
        /// </summary>
        public static string Of(string gameName)
        {
            var sb = new StringBuilder();
            foreach (char c in gameName)
            {
                bool ascii = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                sb.Append(ascii || c == '-' || c == '_' || c == '.' || c == '(' || c == ')' ? c : '_');
            }
            string clean = sb.ToString().Trim('.', '_');
            while (clean.Contains("__")) clean = clean.Replace("__", "_");
            if (clean.Length > 80) clean = clean.Substring(0, 80).TrimEnd('.');
            if (clean.Length == 0) clean = "game";
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(gameName));
                return clean + "-" + BitConverter.ToString(h, 0, 4).Replace("-", "").ToLowerInvariant();
            }
        }
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
