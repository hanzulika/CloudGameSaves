using SaveCloud.Core;
using SaveCloud.Core.Manifest;
using SaveCloud.Core.Scan;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SaveCloud.Tests
{
    public class PathTemplateTests : IDisposable
    {
        private readonly TestPc _pc;

        public PathTemplateTests()
        {
            _pc = new TestPc("P", Path.GetTempPath(), new List<ManifestGame>());
        }

        public void Dispose() => _pc.Dispose();

        private List<SaveFile> Expand(string pattern, params string[] basePaths)
            => new PathTemplate(_pc.Env).Expand(pattern, basePaths, new List<string> { "MyGame" }, 123);

        [Fact]
        public void Folder_IncludesEverythingBelowIt_WithPortableTemplates()
        {
            _pc.Write("winAppData", "MyGame/slot1.sav", "1");
            _pc.Write("winAppData", "MyGame/sub/slot2.sav", "2");

            var files = Expand("<winAppData>/MyGame");
            Assert.Equal(new[] { "<winAppData>/MyGame/slot1.sav", "<winAppData>/MyGame/sub/slot2.sav" },
                         files.Select(f => f.Template).OrderBy(t => t).ToArray());
        }

        [Fact]
        public void Wildcards_AndDoubleStar()
        {
            _pc.Write("winDocuments", "My Games/MyGame/Profiles/A/save.dat", "a");
            _pc.Write("winDocuments", "My Games/MyGame/Profiles/B/deep/save.dat", "b");
            _pc.Write("winDocuments", "My Games/MyGame/Profiles/B/deep/notes.txt", "x");

            var files = Expand("<winDocuments>/My Games/<game>/**/*.dat");
            Assert.Equal(2, files.Count);
            Assert.All(files, f => Assert.EndsWith("save.dat", f.Template));
        }

        [Fact]
        public void BareToken_IsNeverExpanded()
        {
            _pc.Write("home", "whatever.txt", "x");
            Assert.Empty(Expand("<home>"));
        }

        [Fact]
        public void Resolve_RejectsTemplatesThatEscapeTheirFolder()
        {
            var t = new PathTemplate(_pc.Env);
            Assert.Null(t.Resolve("<winAppData>/../../evil.txt", null));
            Assert.Null(t.Resolve("<winAppData>/C:/Windows/evil.txt", null));
            Assert.Null(t.Resolve("<base>/save.sav", new List<string>()));
            Assert.Null(t.Resolve("<notAToken>/x", null));
            Assert.Equal(Path.Combine(_pc.Env.Folders["winAppData"], "MyGame", "a.sav"), t.Resolve("<winAppData>/MyGame/a.sav", null));
        }

        [Fact]
        public void StoreUserId_MatchesAnySteamUser()
        {
            _pc.Env.SteamRoot = _pc.Dir("Steam");
            File.WriteAllText(Path.Combine(_pc.Dir("Steam", "userdata", "998877", "123", "remote"), "save.bin"), "s");
            var files = Expand("<root>/userdata/<storeUserId>/<storeGameId>/remote");
            Assert.Equal("<root>/userdata/998877/123/remote/save.bin", files.Single().Template);
        }

        [Theory]
        [InlineData("save1.sav", "*.sav", true)]
        [InlineData("save1.sav", "save?.sav", true)]
        [InlineData("save10.sav", "save?.sav", false)]
        [InlineData("profile.json", "*.sav", false)]
        [InlineData("anything", "*", true)]
        public void Wildcard(string name, string pattern, bool expected)
        {
            var m = typeof(PathTemplate).Assembly.GetType("SaveCloud.Core.Scan.Wildcard").GetMethod("IsMatch");
            Assert.Equal(expected, (bool)m.Invoke(null, new object[] { name, pattern, true }));
        }

        [Fact]
        public void SharedFiles_GoToTheMostSpecificGame()
        {
            var manifest = new List<ManifestGame>
            {
                new ManifestGame { Name = "Generic", Files = { new FileRule { Pattern = "<winDocuments>/Studio", IsSave = true } } },
                new ManifestGame { Name = "Specific", Files = { new FileRule { Pattern = "<winDocuments>/Studio/Specific/*.sav", IsSave = true } } },
            };
            _pc.Write("winDocuments", "Studio/Specific/a.sav", "a");
            _pc.Write("winDocuments", "Studio/other.sav", "b");

            var scan = new GameScanner(_pc.Env, _pc.Config).ScanAll(manifest);
            Assert.Equal("<winDocuments>/Studio/Specific/a.sav", scan.Games.Single(g => g.Name == "Specific").Files.Single().Template);
            Assert.Equal("<winDocuments>/Studio/other.sav", scan.Games.Single(g => g.Name == "Generic").Files.Single().Template);
        }

        [Fact]
        public void SteamCloudGames_InstalledThroughSteam_AreSkipped()
        {
            var manifest = new List<ManifestGame>
            {
                new ManifestGame { Name = "Cloudy", SteamId = 42, SteamCloud = true, CloudStores = { "steam" },
                    Files = { new FileRule { Pattern = "<winAppData>/Cloudy", IsSave = true } } },
            };
            _pc.Write("winAppData", "Cloudy/s.sav", "x");

            Assert.Single(new GameScanner(_pc.Env, _pc.Config).ScanAll(manifest).Games);
            _pc.Env.SteamInstalledAppIds.Add(42);
            Assert.Empty(new GameScanner(_pc.Env, _pc.Config).ScanAll(manifest).Games);
        }

        [Fact]
        public void ConfigOnlyRules_AreSkippedByDefault()
        {
            var manifest = new List<ManifestGame>
            {
                new ManifestGame { Name = "Cfg", Files = { new FileRule { Pattern = "<winAppData>/Cfg/settings.ini", IsConfig = true } } },
            };
            _pc.Write("winAppData", "Cfg/settings.ini", "fullscreen=1");
            Assert.Empty(new GameScanner(_pc.Env, _pc.Config).ScanAll(manifest).Games);
            _pc.Config.IncludeConfigFiles = true;
            Assert.Single(new GameScanner(_pc.Env, _pc.Config).ScanAll(manifest).Games);
        }
    }
}
