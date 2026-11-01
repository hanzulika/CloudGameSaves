using SaveCloud.Core;
using SaveCloud.Core.Manifest;
using SaveCloud.Core.Remote;
using SaveCloud.Core.Scan;
using SaveCloud.Core.Sync;
using System;
using System.Collections.Generic;
using System.IO;

namespace SaveCloud.Tests
{
    /// <summary>A simulated PC: its own user folders, game library, sync state and engine, sharing one server folder.</summary>
    internal sealed class TestPc : IDisposable
    {
        public string Root { get; }
        public PathEnvironment Env { get; } = new PathEnvironment { IgnoreCase = false };
        public AppConfig Config { get; } = new AppConfig { SkipSteamCloudGames = true };
        public GameScanner Scanner { get; }
        public SyncEngine Engine { get; }
        public HashSet<string> Running { get; } = new HashSet<string>();
        public List<ManifestGame> Manifest { get; }

        public TestPc(string name, string serverDir, List<ManifestGame> manifest, string userName = "player")
            : this(name, () => new FolderTransport(serverDir), manifest, userName) { }

        public TestPc(string name, Func<IRemoteTransport> transport, List<ManifestGame> manifest, string userName = "player")
        {
            Root = Path.Combine(Path.GetTempPath(), "savecloud-tests", Guid.NewGuid().ToString("N"), name);
            Directory.CreateDirectory(Root);
            Manifest = manifest;

            string home = Dir("Users", userName);
            Env.OsUserName = userName;
            Env.Folders["home"] = home;
            Env.Folders["winAppData"] = Dir("Users", userName, "AppData", "Roaming");
            Env.Folders["winLocalAppData"] = Dir("Users", userName, "AppData", "Local");
            Env.Folders["winLocalAppDataLow"] = Dir("Users", userName, "AppData", "LocalLow");
            Env.Folders["winDocuments"] = Dir("Users", userName, "Documents");
            Env.Folders["winPublic"] = Dir("Users", "Public");
            Env.Roots.Add(new GameRoot(Dir("Games"), RootKind.Plain));

            Scanner = new GameScanner(Env, Config);
            var state = SyncState.Load(Path.Combine(Root, "state.json"));
            Engine = new SyncEngine(Config, () => new FileStore(transport()), Scanner, state, g => Running.Contains(g));
            Rescan();
        }

        public string Dir(params string[] parts)
        {
            var p = Path.Combine(Root, Path.Combine(parts));
            Directory.CreateDirectory(p);
            return p;
        }

        public string Write(string folderToken, string relative, string content, DateTime? mtime = null)
        {
            string path = Path.Combine(Env.Folders[folderToken], relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            // Distinct timestamps so the fingerprint cache sees every write.
            File.SetLastWriteTimeUtc(path, mtime ?? DateTime.UtcNow.AddSeconds(Interlocked()));
            return path;
        }

        private static int _tick;
        private static int Interlocked() => System.Threading.Interlocked.Increment(ref _tick);

        public string Read(string folderToken, string relative)
        {
            string path = Path.Combine(Env.Folders[folderToken], relative);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        public ScanResult Rescan()
        {
            var scan = Scanner.ScanAll(Manifest);
            Engine.UpdateLocal(Manifest, scan);
            return scan;
        }

        public List<GameSyncResult> Sync()
        {
            Rescan();
            return Engine.SyncAll();
        }

        public void Dispose()
        {
            try { Directory.Delete(Path.GetDirectoryName(Root), true); } catch { }
        }
    }
}
