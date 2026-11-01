using SaveCloud.Core;
using SaveCloud.Core.Manifest;
using SaveCloud.Core.Remote;
using SaveCloud.Core.Scan;
using SaveCloud.Core.Sync;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SaveCloud.Tests
{
    public class DetectionTests : IDisposable
    {
        private readonly string _server = Path.Combine(Path.GetTempPath(), "savecloud-tests", "det-" + Guid.NewGuid().ToString("N"));

        private readonly List<ManifestGame> _manifest = new List<ManifestGame>
        {
            new ManifestGame { Name = "Hollow Test", Files = { new FileRule { Pattern = "<winAppData>/Hollow", IsSave = true } } },
            new ManifestGame { Name = "The Sims 4", InstallDirs = { "The Sims 4" },
                Files = { new FileRule { Pattern = "<base>/Saves", IsSave = true } } },
            new ManifestGame { Name = "Epic Thing", InstallDirs = { "EpicThing" }, CloudStores = { "epic", "gog" },
                Files = { new FileRule { Pattern = "<winAppData>/EpicThing", IsSave = true } } },
        };

        public DetectionTests()
        {
            Directory.CreateDirectory(_server);
            AppPaths.CacheDir = Path.Combine(_server, "cache");
        }

        public void Dispose() { try { Directory.Delete(_server, true); } catch { } }

        [Fact]
        public void MaliciousUpload_OutsideTheGamesFolders_IsNotRestored()
        {
            using var a = new TestPc("A", _server, _manifest);
            using var b = new TestPc("B", _server, _manifest);
            a.Write("winAppData", "Hollow/slot.sav", "ok");
            a.Sync();

            // Someone else on the shared server publishes a revision that also drops a file into Startup.
            string evilSrc = a.Write("winAppData", "staging/evil.bat", "bad");
            string goodSrc = Path.Combine(a.Env.Folders["winAppData"], "Hollow", "slot.sav");
            var files = new List<SaveFile>
            {
                new SaveFile { ActualPath = goodSrc, Template = "<winAppData>/Hollow/slot.sav" },
                new SaveFile { ActualPath = evilSrc, Template = "<winAppData>/Microsoft/Windows/Start Menu/Programs/Startup/evil.bat" },
            };
            var built = SaveArchive.Create(files, Path.Combine(_server, "evil.zip"));
            using (var store = new FileStore(new FolderTransport(_server)))
            {
                store.Connect();
                store.Publish("Hollow Test", 1, built.Path, built.Sha256, built.Fingerprint, built.Files, 10);
            }

            var r = b.Sync().Single(x => x.Game == "Hollow Test");
            Assert.Equal(SyncAction.Error, r.Action);
            Assert.False(File.Exists(Path.Combine(b.Env.Folders["winAppData"], "Microsoft", "Windows", "Start Menu", "Programs", "Startup", "evil.bat")));
            Assert.Null(b.Read("winAppData", "Hollow/slot.sav"));
        }

        [Fact]
        public void GameAddedByHandOnAnotherPc_NeedsApproval_ThenSyncs()
        {
            using var a = new TestPc("A", _server, _manifest);
            using var b = new TestPc("B", _server, _manifest);
            a.Config.CustomGames.Add(new CustomGame { Name = "Bloodborne", Pattern = "<winAppData>/shadPS4/user/savedata" });
            a.Write("winAppData", "shadPS4/user/savedata/1/CUSA00900/SPRJ0005/userdata0000", "hunter");
            Assert.Equal(SyncAction.Uploaded, a.Sync().Single(x => x.Game == "Bloodborne").Action);

            var r = b.Sync().Single(x => x.Game == "Bloodborne");
            Assert.Equal(SyncAction.NotOnThisPc, r.Action);
            Assert.Null(b.Read("winAppData", "shadPS4/user/savedata/1/CUSA00900/SPRJ0005/userdata0000"));

            var view = b.Engine.Snapshot().Single(g => g.Name == "Bloodborne");
            Assert.Equal(new[] { "<winAppData>/shadPS4/user/savedata" }, view.RemoteRules);
            foreach (var rule in view.RemoteRules) b.Config.CustomGames.Add(new CustomGame { Name = "Bloodborne", Pattern = rule });

            Assert.Equal(SyncAction.Downloaded, b.Sync().Single(x => x.Game == "Bloodborne").Action);
            Assert.Equal("hunter", b.Read("winAppData", "shadPS4/user/savedata/1/CUSA00900/SPRJ0005/userdata0000"));
        }

        [Fact]
        public void InstalledProgramsList_FindsGamesOutsideLibraries_ByFolderOrName()
        {
            using var pc = new TestPc("P", _server, _manifest);
            string sims = pc.Dir("Repacks", "TS4 v1.100");          // folder name doesn't match, display name does
            File.WriteAllText(Path.Combine(pc.Dir("Repacks", "TS4 v1.100", "Saves"), "slot.save"), "sims");
            pc.Env.Installs.Add(new InstallCandidate { Path = sims, DisplayName = "The Sims 4 DX9", Source = "shortcut" });

            var scan = pc.Rescan();
            var game = scan.Games.Single(g => g.Name == "The Sims 4");
            Assert.Equal("<base>/Saves/slot.save", game.Files.Single().Template);
            Assert.Equal(new[] { sims }, scan.InstallPaths["The Sims 4"]);
        }

        [Fact]
        public void LauncherCloudGames_AreSkippedOnlyWhenInstalledThroughThatLauncher()
        {
            using var pc = new TestPc("P", _server, _manifest);
            pc.Write("winAppData", "EpicThing/save.dat", "x");
            string install = pc.Dir("Epic Games", "EpicThing");

            pc.Env.Installs.Add(new InstallCandidate { Path = install, DisplayName = "Epic Thing", Store = "epic", Source = "Epic" });
            var scan = pc.Rescan();
            var skipped = scan.SkippedGames.Single(s => s.Name == "Epic Thing");
            Assert.Equal(SkipReason.LauncherCloud, skipped.Reason);
            Assert.Equal("epic", skipped.Store);

            pc.Config.AlsoSyncCloudGames.Add("Epic Thing");
            Assert.Contains(pc.Rescan().Games, g => g.Name == "Epic Thing");

            pc.Config.AlsoSyncCloudGames.Clear();
            pc.Env.Installs.Clear();
            pc.Env.Installs.Add(new InstallCandidate { Path = install, DisplayName = "Epic Thing", Store = null, Source = "repack" });
            Assert.Contains(pc.Rescan().Games, g => g.Name == "Epic Thing"); // not from a launcher: nothing else syncs it
        }

        [Fact]
        public void Suggestions_FindUnknownSaveFolders_ButNotKnownGamesOrNoise()
        {
            using var pc = new TestPc("P", _server, _manifest);
            pc.Write("winAppData", "Hollow/slot.sav", "known game");
            pc.Write("winDocuments", "Electronic Arts/Les Sims 4/saves/Slot_1.save", "localized");
            pc.Write("winDocuments", "Electronic Arts/Les Sims 4/Mods/huge.package", "mods");
            pc.Write("winLocalAppDataLow", "Tiny Studio/Card Wars/save.dat", "unity");
            pc.Write("winLocalAppData", "GlimpseGame/Saved/SaveGames/slot0.sav", "unreal");
            pc.Write("winDocuments", "Taxes/2025.pdf", "noise");
            pc.Write("winAppData", "Discord/saves/x.json", "app data, not a game");
            string emu = pc.Dir("Steam", "steamapps", "common", "Emulators", "shadPS4"); // ancestors named like apps must not matter
            File.WriteAllText(Path.Combine(pc.Dir("Steam", "steamapps", "common", "Emulators", "shadPS4", "user", "savedata", "1", "CUSA00900"), "slot"), "bloodborne");
            pc.Env.Installs.Add(new InstallCandidate { Path = emu, DisplayName = "Bloodborne", Source = "shortcut" });

            var s = pc.Rescan().Suggestions;
            Assert.Contains(s, x => x.Name == "Les Sims 4" && x.Template == "<winDocuments>/Electronic Arts/Les Sims 4/saves");
            Assert.Contains(s, x => x.Name == "Card Wars" && x.Template == "<winLocalAppDataLow>/Tiny Studio/Card Wars");
            Assert.Contains(s, x => x.Name == "GlimpseGame" && x.Template == "<winLocalAppData>/GlimpseGame/Saved/SaveGames");
            Assert.Contains(s, x => x.Name == "Bloodborne" && x.Folder.EndsWith(Path.Combine("shadPS4", "user", "savedata")));
            Assert.DoesNotContain(s, x => x.Folder.Contains("Hollow"));
            Assert.DoesNotContain(s, x => x.Folder.Contains("Taxes") || x.Folder.Contains("Discord"));

            pc.Config.HiddenSuggestions.Add("<winLocalAppDataLow>/Tiny Studio/Card Wars");
            Assert.DoesNotContain(pc.Rescan().Suggestions, x => x.Name == "Card Wars");
        }

        [Fact]
        public void ToTemplate_UsesTheMostSpecificKnownFolder()
        {
            using var pc = new TestPc("P", _server, _manifest);
            var t = new PathTemplate(pc.Env);
            Assert.Equal("<winAppData>/shadPS4/user/savedata", t.ToTemplate(Path.Combine(pc.Env.Folders["winAppData"], "shadPS4", "user", "savedata")));
            Assert.Equal("<winDocuments>/X", t.ToTemplate(Path.Combine(pc.Env.Folders["winDocuments"], "X") + Path.DirectorySeparatorChar));
            Assert.Equal("<home>/Saved Games/Y", t.ToTemplate(Path.Combine(pc.Env.Folders["home"], "Saved Games", "Y")));
        }

        [Theory]
        [InlineData("<winAppData>/Game/slot1.sav", "<winAppData>/Game", true)]
        [InlineData("<winAppData>/Game/sub/slot1.sav", "<winAppData>/Game/*.sav", false)]
        [InlineData("<winAppData>/Game/slot1.sav", "<winAppData>/Game/*.sav", true)]
        [InlineData("<winAppData>/Game/a/b/c.sav", "<winAppData>/Game/**/*.sav", true)]
        [InlineData("<root>/userdata/123/239140/remote/x", "<root>/userdata/<storeUserId>/239140/remote", true)]
        [InlineData("<winDocuments>/My Games/MyGame/s", "<winDocuments>/My Games/<game>", true)]
        [InlineData("<winDocuments>/My Games/Other/s", "<winDocuments>/My Games/<game>", false)]
        [InlineData("<winAppData>/Microsoft/Windows/Start Menu/Programs/Startup/x.bat", "<winAppData>/Game", false)]
        [InlineData("<winAppData>/Gamex/s", "<winAppData>/Game", false)]
        [InlineData("<winAppData>/Game/s", "<winAppData>", false)]
        public void RuleMatcher_AllowsOnlyFilesInsideTheRule(string template, string rule, bool expected)
            => Assert.Equal(expected, RuleMatcher.Matches(template, rule, new[] { "MyGame" }));

        [Theory]
        [InlineData("The Sims 4 DX9", "The Sims 4")]
        [InlineData("THE SIMS™ 4", "The Sims 4")]
        [InlineData("Epic Thing", "Epic Thing")]
        public void DisplayNames_MatchGames(string display, string game)
        {
            using var pc = new TestPc("P", _server, _manifest);
            string dir = pc.Dir("x", Guid.NewGuid().ToString("N"));
            pc.Env.Installs.Add(new InstallCandidate { Path = dir, DisplayName = display });
            Assert.Equal(new[] { dir }, new GameScanner(pc.Env, pc.Config).FindInstallFolders(_manifest)[game]);
        }

        [Fact]
        public void DisplayNames_DontMatchUnrelatedPrograms()
        {
            using var pc = new TestPc("P", _server, _manifest);
            foreach (var n in new[] { "Steam", "Discord", "Google Chrome", "The Simpsons" })
                pc.Env.Installs.Add(new InstallCandidate { Path = pc.Dir("apps", n), DisplayName = n });
            Assert.Empty(new GameScanner(pc.Env, pc.Config).FindInstallFolders(_manifest));
        }
    }
}
