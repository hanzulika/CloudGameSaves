using SaveCloud.Core;
using SaveCloud.Core.Manifest;
using SaveCloud.Core.Remote;
using SaveCloud.Core.Sync;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SaveCloud.Tests
{
    /// <summary>
    /// Runs against a real SFTP server configured like docs/SERVER_SETUP.md. Skipped unless
    /// SAVECLOUD_SFTP_HOST, SAVECLOUD_SFTP_PORT, SAVECLOUD_SFTP_USER and SAVECLOUD_SFTP_KEY are set.
    /// </summary>
    public class SftpIntegrationTests
    {
        private static readonly string Host = Environment.GetEnvironmentVariable("SAVECLOUD_SFTP_HOST");
        private static readonly int Port = int.TryParse(Environment.GetEnvironmentVariable("SAVECLOUD_SFTP_PORT"), out var p) ? p : 22;
        private static readonly string User = Environment.GetEnvironmentVariable("SAVECLOUD_SFTP_USER");
        private static readonly string Key = Environment.GetEnvironmentVariable("SAVECLOUD_SFTP_KEY");
        private static bool Enabled => !string.IsNullOrEmpty(Host) && !string.IsNullOrEmpty(Key);

        private readonly string _root = "/data/test-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        public SftpIntegrationTests()
        {
            AppPaths.CacheDir = Path.Combine(Path.GetTempPath(), "savecloud-tests", "sftp-cache-" + Guid.NewGuid().ToString("N"));
        }

        private SftpTransport Transport(string pinned = "") =>
            new SftpTransport(Host, Port, User, Key, null, null, _root, pinned);

        [Fact]
        public void TwoPcs_SyncThroughSftp()
        {
            if (!Enabled) return;
            var manifest = new List<ManifestGame>
            {
                new ManifestGame { Name = "Sftp Game: Remastered", Files = { new FileRule { Pattern = "<winAppData>/SftpGame", IsSave = true } } },
            };
            using var a = new TestPc("A", () => Transport(), manifest);
            using var b = new TestPc("B", () => Transport(), manifest);

            a.Write("winAppData", "SftpGame/slot1.sav", "from A");
            a.Write("winAppData", "SftpGame/profiles/p.json", "{}");
            Assert.Equal(SyncAction.Uploaded, a.Sync().Single().Action);
            Assert.Equal(SyncAction.Downloaded, b.Sync().Single().Action);
            Assert.Equal("from A", b.Read("winAppData", "SftpGame/slot1.sav"));

            b.Write("winAppData", "SftpGame/slot1.sav", "from B");
            Assert.Equal(SyncAction.Uploaded, b.Sync().Single().Action);       // revision 2 replaces head.json atomically
            Assert.Equal(SyncAction.Downloaded, a.Sync().Single().Action);
            Assert.Equal("from B", a.Read("winAppData", "SftpGame/slot1.sav"));

            a.Write("winAppData", "SftpGame/slot1.sav", "A again");
            a.Sync();
            b.Write("winAppData", "SftpGame/slot1.sav", "B diverged");
            Assert.Equal(SyncAction.Conflict, b.Sync().Single().Action);
            Assert.Equal(SyncAction.Uploaded, b.Engine.Resolve("Sftp Game: Remastered", keepLocal: true).Action);
        }

        [Fact]
        public void Lock_IsExclusive_OverSftp()
        {
            if (!Enabled) return;
            using var s1 = new RemoteStore(Transport());
            using var s2 = new RemoteStore(Transport());
            s1.Connect();
            s2.Connect();
            using (s1.Lock("Locked Game", TimeSpan.FromSeconds(1)))
                Assert.Throws<LockTimeoutException>(() => s2.Lock("Locked Game", TimeSpan.FromSeconds(2)));
            s2.Lock("Locked Game", TimeSpan.FromSeconds(1)).Dispose();
        }

        [Fact]
        public void HostKey_IsPinned_AndAMismatchIsRefused()
        {
            if (!Enabled) return;
            string seen = null;
            using (var t = Transport())
            {
                t.HostKeyFirstSeen += fp => seen = fp;
                t.Connect();
            }
            Assert.False(string.IsNullOrEmpty(seen));

            using (var ok = Transport(seen)) ok.Connect();
            using (var bad = Transport("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"))
                Assert.Throws<HostKeyMismatchException>(() => bad.Connect());
        }
    }
}
