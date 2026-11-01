using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SaveCloud.Core.Remote
{
    /// <summary>Stores everything in a local or network folder (e.g. \\nas\saves). Also used by the tests.</summary>
    public class FolderTransport : IRemoteTransport
    {
        private readonly string _root;

        public FolderTransport(string root) { _root = root; }

        private string Full(string path)
        {
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(p => p == ".." || p == ".")) throw new ArgumentException("Invalid remote path: " + path);
            return parts.Length == 0 ? _root : Path.Combine(_root, Path.Combine(parts));
        }

        public void Connect()
        {
            if (!Directory.Exists(_root)) throw new DirectoryNotFoundException("Save folder not found: " + _root);
        }

        public bool Exists(string path) => File.Exists(Full(path)) || Directory.Exists(Full(path));

        public List<RemoteEntry> List(string path)
        {
            var dir = new DirectoryInfo(Full(path));
            if (!dir.Exists) return new List<RemoteEntry>();
            return dir.GetFileSystemInfos().Select(ToEntry).ToList();
        }

        public RemoteEntry Stat(string path)
        {
            string full = Full(path);
            if (File.Exists(full)) return ToEntry(new FileInfo(full));
            if (Directory.Exists(full)) return ToEntry(new DirectoryInfo(full));
            return null;
        }

        private static RemoteEntry ToEntry(FileSystemInfo i) => new RemoteEntry
        {
            Name = i.Name,
            IsDirectory = i is DirectoryInfo,
            Size = i is FileInfo f ? f.Length : 0,
            ModifiedUtc = i.LastWriteTimeUtc,
        };

        public void Read(string path, Stream destination)
        {
            using (var fs = File.OpenRead(Full(path))) fs.CopyTo(destination);
        }

        public void Write(string path, Stream source)
        {
            using (var fs = File.Create(Full(path))) source.CopyTo(fs);
        }

        public void RenameReplace(string from, string to)
        {
            string src = Full(from), dst = Full(to);
            if (File.Exists(dst)) File.Replace(src, dst, null);
            else File.Move(src, dst);
        }

        public void Delete(string path)
        {
            string full = Full(path);
            if (File.Exists(full)) File.Delete(full);
        }

        public void EnsureDirectory(string path) => Directory.CreateDirectory(Full(path));

        public bool TryCreateDirectoryExclusive(string path)
        {
            // Directory.CreateDirectory succeeds silently if the folder exists, so use a marker file
            // created with CreateNew, which is atomic on local disks and SMB shares.
            string full = Full(path);
            Directory.CreateDirectory(full);
            try
            {
                using (new FileStream(Path.Combine(full, ".owner"), FileMode.CreateNew)) { }
                return true;
            }
            catch (IOException) when (File.Exists(Path.Combine(full, ".owner")))
            {
                return false;
            }
        }

        public void DeleteDirectory(string path)
        {
            string full = Full(path);
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }

        public void Dispose() { }
    }
}
