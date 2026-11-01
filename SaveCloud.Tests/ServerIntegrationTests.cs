using SaveCloud.Core;
using SaveCloud.Core.Manifest;
using SaveCloud.Core.Remote;
using SaveCloud.Core.Sync;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Xunit;

namespace SaveCloud.Tests
{
    /// <summary>
    /// End to end: the real Python server (server/savecloud_server.py) on loopback, and two simulated PCs syncing
    /// through it with <see cref="HttpStore"/>. Skipped when no Python 3 is installed.
    /// </summary>
    public sealed class ServerIntegrationTests : IDisposable
    {
        private readonly Process _server;
        private readonly string _dir;
        private readonly string _url;

        private readonly List<ManifestGame> _manifest = new List<ManifestGame>
        {
            new ManifestGame { Name = "Pokémon: Ünicode Edition", Files = { new FileRule { Pattern = "<winAppData>/Uni", IsSave = true } } },
            new ManifestGame { Name = ".hack//G.U. Last Recode", Files = { new FileRule { Pattern = "<winAppData>/Hack", IsSave = true } } },
        };

        public ServerIntegrationTests()
        {
            AppPaths.CacheDir = Path.Combine(Path.GetTempPath(), "savecloud-tests", "http-cache-" + Guid.NewGuid().ToString("N"));
            _dir = Path.Combine(Path.GetTempPath(), "savecloud-tests", "http-server-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "allowed"), "tester@example.com\n");

            string script = FindServerScript();
            string python = FindPython();
            if (script == null || python == null) return;

            int port = FreePort();
            _url = $"http://127.0.0.1:{port}";
            var psi = new ProcessStartInfo(python,
                $"\"{script}\" --data \"{Path.Combine(_dir, "data")}\" --allow-file \"{Path.Combine(_dir, "allowed")}\" " +
                $"--bind 127.0.0.1 --port {port} --test-login tester@example.com")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            _server = Process.Start(psi);
            var ready = new ManualResetEventSlim();
            _server.OutputDataReceived += (s, e) => { if (e.Data == "READY") ready.Set(); };
            _server.BeginOutputReadLine();
            _server.BeginErrorReadLine();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(20)), "server did not start");
        }

        public void Dispose()
        {
            try { if (_server != null && !_server.HasExited) _server.Kill(); } catch { }
            try { Directory.Delete(_dir, true); } catch { }
        }

        private bool Enabled => _server != null;

        [Fact]
        public void TwoPcs_SyncThroughTheRealServer_IncludingUnicodeNames()
        {
            if (!Enabled) return;
            using var a = new TestPc("A", () => new FolderTransport(_dir), _manifest);
            using var b = new TestPc("B", () => new FolderTransport(_dir), _manifest);
            var engineA = new SyncEngine(a.Config, () => new HttpStore(_url), a.Scanner, SyncState.Load(Path.Combine(a.Root, "s.json")), _ => false);
            var engineB = new SyncEngine(b.Config, () => new HttpStore(_url), b.Scanner, SyncState.Load(Path.Combine(b.Root, "s.json")), _ => false);

            a.Write("winAppData", "Uni/save.dat", "gotta catch em");
            a.Write("winAppData", "Hack/slot.bin", "dot hack");
            engineA.UpdateLocal(_manifest, a.Rescan());
            Assert.All(engineA.SyncAll(), r => Assert.Equal(SyncAction.Uploaded, r.Action));

            engineB.UpdateLocal(_manifest, b.Rescan());
            Assert.All(engineB.SyncAll(), r => Assert.Equal(SyncAction.Downloaded, r.Action));
            Assert.Equal("gotta catch em", b.Read("winAppData", "Uni/save.dat"));
            Assert.Equal("dot hack", b.Read("winAppData", "Hack/slot.bin"));

            // B plays on; A gets it. Then both change: conflict, resolved by keeping B's.
            b.Write("winAppData", "Uni/save.dat", "B progress");
            engineB.UpdateLocal(_manifest, b.Rescan());
            Assert.Equal(SyncAction.Uploaded, engineB.SyncGame(_manifest[0].Name).Action);
            engineA.UpdateLocal(_manifest, a.Rescan());
            Assert.Equal(SyncAction.Downloaded, engineA.SyncGame(_manifest[0].Name).Action);
            Assert.Equal("B progress", a.Read("winAppData", "Uni/save.dat"));

            a.Write("winAppData", "Uni/save.dat", "A again");
            engineA.UpdateLocal(_manifest, a.Rescan());
            engineA.SyncGame(_manifest[0].Name);
            b.Write("winAppData", "Uni/save.dat", "B diverged");
            engineB.UpdateLocal(_manifest, b.Rescan());
            Assert.Equal(SyncAction.Conflict, engineB.SyncGame(_manifest[0].Name).Action);
            Assert.Equal(SyncAction.Uploaded, engineB.Resolve(_manifest[0].Name, keepLocal: true).Action);

            var head = new HttpStore(_url).ReadHead(_manifest[0].Name);
            Assert.Equal("tester@example.com", head.UploadedLogin);
            Assert.Equal(Environment.MachineName, head.UploadedBy);
        }

        [Fact]
        public async System.Threading.Tasks.Task Probe_ReportsIdentityAndMode_AndConflictsSurfaceAsExceptions()
        {
            if (!Enabled) return;
            var info = await HttpStore.ProbeAsync(_url, TimeSpan.FromSeconds(5));
            Assert.True(info.Authorized);
            Assert.Equal("tester@example.com", info.Login);
            Assert.Equal("shared", info.Mode);
            Assert.Null(await HttpStore.ProbeAsync("http://127.0.0.1:1", TimeSpan.FromSeconds(2)));

            var store = new HttpStore(_url);
            store.Connect();
            string zip = Path.Combine(_dir, "x.zip");
            File.WriteAllBytes(zip, new byte[] { 1, 2, 3 });
            string sha = Hashing.FileSha256(zip);
            store.Publish("Race", 0, zip, sha, "f1", new List<RemoteFileEntry>(), 10);
            var ex = Assert.Throws<RevisionConflictException>(() => store.Publish("Race", 0, zip, sha, "f2", new List<RemoteFileEntry>(), 10));
            Assert.Equal(1, ex.Current.Revision);
        }

        private static string FindServerScript()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                string p = Path.Combine(d.FullName, "server", "savecloud_server.py");
                if (File.Exists(p)) return p;
                d = d.Parent;
            }
            return null;
        }

        private static string FindPython()
        {
            foreach (var name in new[] { "python3", "python" })
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo(name, "--version") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
                    p.WaitForExit(5000);
                    if (p.ExitCode == 0) return name;
                }
                catch { }
            }
            return null;
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }
}
