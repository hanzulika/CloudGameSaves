using System;
using System.Collections.Generic;
using System.IO;

namespace SaveCloud.Core.Remote
{
    public class RemoteEntry
    {
        public string Name { get; set; }
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTime ModifiedUtc { get; set; }
    }

    /// <summary>
    /// The handful of file operations the sync protocol needs. Paths are '/'-separated and relative to the
    /// remote root. Implemented over SFTP (the real server) and a plain folder (tests, or a network share).
    /// </summary>
    public interface IRemoteTransport : IDisposable
    {
        void Connect();
        bool Exists(string path);
        /// <summary>Lists a directory; returns an empty list if it doesn't exist.</summary>
        List<RemoteEntry> List(string path);
        RemoteEntry Stat(string path);
        void Read(string path, Stream destination);
        /// <summary>Creates or overwrites a file.</summary>
        void Write(string path, Stream source);
        /// <summary>Renames, replacing the destination atomically if it exists.</summary>
        void RenameReplace(string from, string to);
        void Delete(string path);
        /// <summary>Creates a directory and its parents; no error if it already exists.</summary>
        void EnsureDirectory(string path);
        /// <summary>Creates a directory, returning false if it already exists. Must be atomic: this is the lock primitive.</summary>
        bool TryCreateDirectoryExclusive(string path);
        void DeleteDirectory(string path);
    }
}
