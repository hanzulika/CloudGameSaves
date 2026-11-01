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
    public class SyncEngineTests : IDisposable
    {
        private readonly string _server;
        private readonly List<ManifestGame> _manifest = new List<ManifestGame>
        {
            new ManifestGame
            {
                Name = "Hollow Test",
                InstallDirs = { "HollowTest" },
                Files = { new FileRule { Pattern = "<winLocalAppDataLow>/Team Test/Hollow Test/*.dat", IsSave = true } },
            },
            new ManifestGame
            {
                Name = "Base Game",
                InstallDirs = { "BaseGame" },
                Files = { new FileRule { Pattern = "<base>/Saves", IsSave = true } },
            },
            new ManifestGame
            {
                Name = "User Named",
                Files = { new FileRule { Pattern = "<winDocuments>/UserNamed/<osUserName>/save.bin", IsSave = true } },
            },
        };

        public SyncEngineTests()
        {
            _server = Path.Combine(Path.GetTempPath(), "savecloud-tests", "server-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_server);
            AppPaths.CacheDir = Path.Combine(_server, "..", "cache-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_server, true); } catch { }
        }

        private const string Hollow = "Team Test/Hollow Test/";

        private static GameSyncResult For(IEnumerable<GameSyncResult> results, string game) => results.Single(r => r.Game == game);

        [Fact]
        public void FirstPcUploads_SecondPcDownloads()
        {
            using var a = new TestPc("A", _server, _manifest);
            using var b = new TestPc("B", _server, _manifest);

            a.Write("winLocalAppDataLow", Hollow + "user1.dat", "slot one");
            a.Write("winLocalAppDataLow", Hollow + "user2.dat", "slot two");

            Assert.Equal(SyncAction.Uploaded, For(a.Sync(), "Hollow Test").Action);
            Assert.Equal(SyncAction.Downloaded, For(b.Sync(), "Hollow Test").Action);

            Assert.Equal("slot one", b.Read("winLocalAppDataLow", Hollow + "user1.dat"));
            Assert.Equal("slot two", b.Read("winLocalAppDataLow", Hollow + "user2.dat"));

            // Nothing new on either side: both are in sync and nothing is transferred again.
            Assert.Equal(SyncAction.InSync, For(a.Sync(), "Hollow Test").Action);
            Assert.Equal(SyncAction.InSync, For(b.Sync(), "Hollow Test").Action);
        }

        [Fact]
        public void PlayOnOnePc_ThenTheOtherGetsOnlyTheNewRevision()
        {
            using var a = new TestPc("A", _server, _manifest);
            using var b = new TestPc("B", _server, _manifest);

            a.Write("winLocalAppDataLow", Hollow + "user1.dat", "v1");
            a.Sync();
            b.Sync();

            b.Write("winLocalAppDataLow", Hollow + "user1.dat", "v2 played on B");
            Assert.Equal(SyncAction.Uploaded, For(b.Sync(), "Hollow Test").Action);

            Assert.Equal(SyncAction.Downloaded, For(a.Sync(), "Hollow Test").Action);
            Assert.Equal("v2 played on B", a.Read("winLocalAppDataLow", Hollow + "user1.dat"));
            Assert.Equal(2, a.Engine.State.Get("Hollow Test").BaseRevision);
        }

        [Fact]
        public void BothChanged_IsAConflict_AndNothingIsOverwrittenUntilResolved()
        {
            using var a = new TestPc("A", _server, _manifest);
            using var b = new TestPc("B", _server, _manifest);

            a.Write("winLocalAppDataLow", Hollow + "user1.dat", "v1");
            a.Sync();
            b.Sync();

            a.Write("winLocalAppDataLow", Hollow + "user1.dat", "A's progress");
            a.Sync();
            b.Write("winLocalAppDataLow", Hollow + "user1.dat", "B's progress");

            var r = For(b.Sync(), "Hollow Test");
            Assert.Equal(SyncAction.Conflict, r.Action);
            Assert.Equal("B's progress", b.Read("winLocalAppDataLow", Hollow + "user1.dat"));
            Assert.NotNull(b.Engine.State.Get("Hollow Test").Conflict);

            // Still a conflict on the next round, still untouched.
            Assert.Equal(SyncAction.Conflict, For(b.Sync(), "Hollow Test").Action);
            Assert.Equal("B's progress", b.Read("winLocalAppDataLow", Hollow + "user1.dat"));

            // Choose the server copy: B gets A's progress, and B's own version is kept in a local backup.
            Assert.Equal(SyncAction.Downloaded, b.Engine.Resolve("Hollow Test", keepLocal: false).Action);
            Assert.Equal("A's progress", b.Read("winLocalAppDataLow", Hollow + "user1.dat"));
            Assert.Null(b.Engine.State.Get("Hollow Test").Conflict);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(AppPaths.BackupDir, RemoteStore.Slug("Hollow Test"))));
        }

        [Fact]
        public void Conflict_KeepLocal_UploadsOverServer()
        {
            using var a = new TestPc("A", _server, _manifest);
            using var b = new TestPc("B", _server, _manifest);

            a.Write("winLocalAppDataLow", Hollow + "user1.dat", "A");
            a.Sync();
            b.Write("winLocalAppDataLow", Hollow + "user1.dat", "B already had a different save");

            Assert.Equal(SyncAction.Conflict, For(b.Sync(), "Hollow Test").Action);
            Assert.Equal(SyncAction.Uploaded, b.Engine.Resolve("Hollow Test", keepLocal: true).Action);

            Assert.Equal(SyncAction.Downloaded, For(a.Sync(), "Hollow Test").Action);
            Assert.Equal("B already had a different save", a.Read("winLocalAppDataLow", Hollow + "user1.dat"));
        }

        [Fact]
        public void DeletedSaveSlot_IsDeletedOnTheOtherPc()
        {
            using var a = new TestPc("A", _server, _manifest);
            using var b = new TestPc("B", _server, _manifest);

            string slot2 = a.Write("winLocalAppDataLow", Hollow + "user2.dat", "old slot");
            a.Write("winLocalAppDataLow", Hollow + "user1.dat", "keep");
            a.Sync();
            b.Sync();
            Assert.NotNull(b.Read("winLocalAppDataLow", Hollow + "user2.dat"));

            File.Delete(slot2);
            Assert.Equal(SyncAction.Uploaded, For(a.Sync(), "Hollow Test").Action);
            Assert.Equal(SyncAction.Downloaded, For(b.Sync(), "Hollow Test").Action);
            Assert.Null(b.Read("winLocalAppDataLow", Hollow + "user2.dat"));
            Assert.Equal("keep", b.Read("winLocalAppDataLow", Hollow + "user1.dat"));
        }

        [Fact]
        public void AllLocalSavesGone_DoesNotWipeTheServer()
        {
            using var a = new TestPc("A", _server, _manifest);
            string f = a.Write("winLocalAppDataLow", Hollow + "user1.dat", "precious");
            a.Sync();
            File.Delete(f);

            Assert.Equal(SyncAction.LocalMissing, For(a.Sync(), "Hollow Test").Action);
            using var b = new TestPc("B", _server, _manifest);
            Assert.Equal(SyncAction.Downloaded, For(b.Sync(), "Hollow Test").Action);
            Assert.Equal("precious", b.Read("winLocalAppDataLow", Hollow + "user1.dat"));
        }

        [Fact]
        public void RunningGame_IsNotTouched()
        {
            using var a = new TestPc("A", _server, _manifest);
            using var b = new TestPc("B", _server, _manifest);
            a.Write("winLocalAppDataLow", Hollow + "user1.dat", "v1");
            a.Sync();

            b.Running.Add("Hollow Test");
            Assert.Equal(SyncAction.Deferred, For(b.Sync(), "Hollow Test").Action);
            Assert.Null(b.Read("winLocalAppDataLow", Hollow + "user1.dat"));

            b.Running.Clear();
            Assert.Equal(SyncAction.Downloaded, b.Engine.SyncGame("Hollow Test").Action);
        }

        [Fact]
        public void InstallFolderSaves_WaitUntilTheGameIsInstalled()
        {
            using var a = new TestPc("A", _server, _manifest);
            using var b = new TestPc("B", _server, _manifest);

            File.WriteAllText(Path.Combine(a.Dir("Games", "BaseGame", "Saves"), "s.sav"), "in install dir");
            Assert.Equal(SyncAction.Uploaded, For(a.Sync(), "Base Game").Action);

            Assert.Equal(SyncAction.NotInstalled, For(b.Sync(), "Base Game").Action);

            b.Dir("Games", "BaseGame"); // installed on B, no saves yet
            Assert.Equal(SyncAction.Downloaded, For(b.Sync(), "Base Game").Action);
            Assert.Equal("in install dir", File.ReadAllText(Path.Combine(b.Root, "Games", "BaseGame", "Saves", "s.sav")));
        }

        [Fact]
        public void DifferentWindowsUsernames_RestoreUnderTheLocalUser()
        {
            using var a = new TestPc("A", _server, _manifest, userName: "alice");
            using var b = new TestPc("B", _server, _manifest, userName: "bob");

            a.Write("winDocuments", "UserNamed/alice/save.bin", "progress");
            a.Sync();
            Assert.Equal(SyncAction.Downloaded, For(b.Sync(), "User Named").Action);
            Assert.Equal("progress", b.Read("winDocuments", "UserNamed/bob/save.bin"));
            Assert.Equal(SyncAction.InSync, For(b.Sync(), "User Named").Action);
        }

        [Fact]
        public void History_IsPrunedToTheConfiguredCount()
        {
            using var a = new TestPc("A", _server, _manifest);
            a.Config.HistoryToKeep = 3;
            for (int i = 0; i < 6; i++)
            {
                a.Write("winLocalAppDataLow", Hollow + "user1.dat", "rev " + i);
                Assert.Equal(SyncAction.Uploaded, For(a.Sync(), "Hollow Test").Action);
            }
            var revs = Directory.GetFiles(Path.Combine(_server, "games", RemoteStore.Slug("Hollow Test")), "rev-*.zip")
                .Select(Path.GetFileName).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "rev-000004.zip", "rev-000005.zip", "rev-000006.zip" }, revs);
        }

        [Fact]
        public void LostLocalState_FallsBackToContentComparison()
        {
            using var a = new TestPc("A", _server, _manifest);
            a.Write("winLocalAppDataLow", Hollow + "user1.dat", "same");
            a.Sync();
            File.Delete(Path.Combine(a.Root, "state.json"));

            using var a2 = new TestPc("A2", _server, _manifest);
            a2.Write("winLocalAppDataLow", Hollow + "user1.dat", "same");
            // Same content as the server: recognised as in sync, no conflict and no re-upload.
            Assert.Equal(SyncAction.InSync, For(a2.Sync(), "Hollow Test").Action);
        }

        [Fact]
        public void StaleLock_IsBroken_ActiveLock_Waits()
        {
            using var store = new RemoteStore(new FolderTransport(_server));
            store.Connect();
            var held = store.Lock("Hollow Test", TimeSpan.FromSeconds(1));
            Assert.Throws<LockTimeoutException>(() => store.Lock("Hollow Test", TimeSpan.FromSeconds(1)));
            held.Dispose();
            store.Lock("Hollow Test", TimeSpan.FromSeconds(1)).Dispose();

            // Simulate a PC that crashed while holding the lock 20 minutes ago.
            var crashed = store.Lock("Hollow Test", TimeSpan.FromSeconds(1));
            var lockDir = Path.Combine(_server, "games", RemoteStore.Slug("Hollow Test"), ".lock");
            Directory.SetLastWriteTimeUtc(lockDir, DateTime.UtcNow.AddMinutes(-20));
            store.Lock("Hollow Test", TimeSpan.FromSeconds(1)).Dispose();
        }
    }
}
