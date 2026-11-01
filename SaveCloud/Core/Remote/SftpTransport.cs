using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SaveCloud.Core.Remote
{
    public class HostKeyMismatchException : Exception
    {
        public HostKeyMismatchException(string expected, string actual)
            : base($"The server's identity changed (expected host key SHA256:{expected}, got SHA256:{actual}). " +
                   "If you rebuilt the server on purpose, clear the pinned host key in Settings; otherwise someone may be intercepting the connection.")
        { }
    }

    /// <summary>SFTP access to the save server. Uses key auth when a key is configured, otherwise a password.</summary>
    public class SftpTransport : IRemoteTransport
    {
        private readonly string _host;
        private readonly int _port;
        private readonly string _user;
        private readonly string _keyPath;
        private readonly string _keyPassphrase;
        private readonly string _password;
        private readonly string _root;
        private string _pinnedFingerprint;
        private SftpClient _client;
        private string _mismatchSeen;

        /// <summary>Raised when the server's host key is seen for the first time, so the caller can pin it.</summary>
        public event Action<string> HostKeyFirstSeen;

        public SftpTransport(string host, int port, string user, string keyPath, string keyPassphrase,
                             string password, string remoteRoot, string pinnedFingerprint)
        {
            _host = host;
            _port = port;
            _user = user;
            _keyPath = keyPath;
            _keyPassphrase = keyPassphrase;
            _password = password;
            _root = "/" + (remoteRoot ?? "").Trim().Trim('/');
            if (_root == "/") _root = "";
            _pinnedFingerprint = pinnedFingerprint;
        }

        public void Connect()
        {
            var methods = new List<AuthenticationMethod>();
            if (!string.IsNullOrEmpty(_keyPath))
            {
                var key = string.IsNullOrEmpty(_keyPassphrase) ? new PrivateKeyFile(_keyPath) : new PrivateKeyFile(_keyPath, _keyPassphrase);
                methods.Add(new PrivateKeyAuthenticationMethod(_user, key));
            }
            if (!string.IsNullOrEmpty(_password))
                methods.Add(new PasswordAuthenticationMethod(_user, _password));
            if (methods.Count == 0)
                throw new InvalidOperationException("No SSH key or password configured.");

            var info = new ConnectionInfo(_host, _port, _user, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(15) };
            _client = new SftpClient(info) { OperationTimeout = TimeSpan.FromMinutes(2) };
            _client.HostKeyReceived += OnHostKey;
            try
            {
                _client.Connect();
            }
            catch (SshConnectionException) when (_mismatchSeen != null)
            {
                throw new HostKeyMismatchException(_pinnedFingerprint, _mismatchSeen);
            }
        }

        private void OnHostKey(object sender, HostKeyEventArgs e)
        {
            string fp = e.FingerPrintSHA256;
            if (string.IsNullOrEmpty(_pinnedFingerprint))
            {
                _pinnedFingerprint = fp;
                e.CanTrust = true;
                HostKeyFirstSeen?.Invoke(fp);
            }
            else if (string.Equals(_pinnedFingerprint.Replace("SHA256:", "").TrimEnd('='), fp.TrimEnd('='), StringComparison.Ordinal))
            {
                e.CanTrust = true;
            }
            else
            {
                _mismatchSeen = fp;
                e.CanTrust = false;
            }
        }

        private string Full(string path)
        {
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(p => p == ".." || p == ".")) throw new ArgumentException("Invalid remote path: " + path);
            return _root + "/" + string.Join("/", parts);
        }

        public bool Exists(string path) => _client.Exists(Full(path));

        public List<RemoteEntry> List(string path)
        {
            try
            {
                return _client.ListDirectory(Full(path))
                    .Where(f => f.Name != "." && f.Name != "..")
                    .Select(ToEntry)
                    .ToList();
            }
            catch (SftpPathNotFoundException)
            {
                return new List<RemoteEntry>();
            }
        }

        public RemoteEntry Stat(string path)
        {
            try
            {
                var a = _client.GetAttributes(Full(path));
                return new RemoteEntry
                {
                    Name = path.Split('/').Last(),
                    IsDirectory = a.IsDirectory,
                    Size = a.Size,
                    ModifiedUtc = a.LastWriteTimeUtc,
                };
            }
            catch (SftpPathNotFoundException)
            {
                return null;
            }
        }

        private static RemoteEntry ToEntry(ISftpFile f) => new RemoteEntry
        {
            Name = f.Name,
            IsDirectory = f.IsDirectory,
            Size = f.Length,
            ModifiedUtc = f.LastWriteTimeUtc,
        };

        public void Read(string path, Stream destination) => _client.DownloadFile(Full(path), destination);

        public void Write(string path, Stream source) => _client.UploadFile(source, Full(path), true);

        public void RenameReplace(string from, string to)
        {
            // posix-rename@openssh.com replaces the target atomically; plain SFTP rename fails if it exists.
            try
            {
                _client.RenameFile(Full(from), Full(to), true);
            }
            catch (NotSupportedException)
            {
                if (_client.Exists(Full(to))) _client.DeleteFile(Full(to));
                _client.RenameFile(Full(from), Full(to));
            }
        }

        public void Delete(string path)
        {
            try { _client.DeleteFile(Full(path)); }
            catch (SftpPathNotFoundException) { }
        }

        public void EnsureDirectory(string path)
        {
            // Walk from "/" so the configured remote root itself is created too (e.g. /data/saves).
            string current = "";
            foreach (var part in Full(path).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current += "/" + part;
                if (_client.Exists(current)) continue;
                try { _client.CreateDirectory(current); }
                catch (SshException) when (_client.Exists(current)) { } // created concurrently by another PC
            }
        }

        public bool TryCreateDirectoryExclusive(string path)
        {
            // SFTP mkdir fails if the directory exists, and the server performs it atomically.
            try
            {
                _client.CreateDirectory(Full(path));
                return true;
            }
            catch (SshException) when (_client.Exists(Full(path)))
            {
                return false;
            }
        }

        public void DeleteDirectory(string path)
        {
            string full = Full(path);
            if (!_client.Exists(full)) return;
            foreach (var f in _client.ListDirectory(full))
            {
                if (f.Name == "." || f.Name == "..") continue;
                if (f.IsDirectory) DeleteDirectory(path + "/" + f.Name);
                else _client.DeleteFile(f.FullName);
            }
            _client.DeleteDirectory(full);
        }

        public void Dispose()
        {
            if (_client == null) return;
            try { if (_client.IsConnected) _client.Disconnect(); } catch { }
            _client.Dispose();
            _client = null;
        }
    }
}
