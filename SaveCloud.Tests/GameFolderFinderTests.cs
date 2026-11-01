using SaveCloud.Core.Manifest;
using SaveCloud.Core.Scan;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SaveCloud.Tests
{
    public class GameFolderFinderTests : IDisposable
    {
        private readonly string _drive = Path.Combine(Path.GetTempPath(), "savecloud-tests", "drive-" + Guid.NewGuid().ToString("N"), "C");

        public void Dispose() { try { Directory.Delete(Path.GetDirectoryName(_drive), true); } catch { } }

        private string Dir(params string[] parts)
        {
            var p = Path.Combine(_drive, Path.Combine(parts));
            Directory.CreateDirectory(p);
            return p;
        }

        private void File_(string dir, string name, string content = "x") => File.WriteAllText(Path.Combine(dir, name), content);

        private List<ManifestGame> Manifest => new List<ManifestGame>
        {
            new ManifestGame { Name = "Foundation", InstallDirs = { "Foundation" }, SteamId = 690830,
                Files = { new FileRule { Pattern = "<base>/Saves", IsSave = true } } },
            new ManifestGame { Name = "Timberborn", InstallDirs = { "Timberborn" }, SteamId = 1062090,
                Files = { new FileRule { Pattern = "<winDocuments>/Timberborn/Saves", IsSave = true } } },
        };

        [Fact]
        public void FindsGamesAnywhere_ByWhatsInside_AndIdentifiesThem()
        {
            // C:\Games\Foundation v1.9.0.5 [Repack]: Unity game, name only in app.info and the folder.
            string foundation = Dir("Games", "Foundation v1.9.0.5 [Repack]");
            File_(foundation, "Foundation.exe");
            File_(Dir("Games", "Foundation v1.9.0.5 [Repack]", "Foundation_Data"), "app.info", "Polymorph Games\nFoundation\n");
            File_(Dir("Games", "Foundation v1.9.0.5 [Repack]", "Saves"), "town.sav");

            // D:\Stuff\TB: folder name says nothing; steam_appid.txt next to the exe in bin\ does.
            string tb = Dir("Stuff", "TB");
            File_(Dir("Stuff", "TB", "bin"), "game.exe");
            File_(Path.Combine(tb, "bin"), "steam_api64.dll");
            File_(Path.Combine(tb, "bin"), "steam_appid.txt", "1062090\n");

            // An unknown Unity game on the desktop, nested a few levels deep.
            string unknown = Dir("Users", "me", "Desktop", "games", "Card Wars Kingdom");
            File_(unknown, "CardWars.exe");
            File_(unknown, "UnityPlayer.dll");

            // Not games: a plain app, and anything under AppData.
            File_(Dir("Tools", "MyApp"), "MyApp.exe");
            File_(Dir("Users", "me", "AppData", "Local", "Hidden Game"), "UnityPlayer.dll");

            var found = new GameFolderFinder().Find(new[] { _drive });
            Assert.Equal(new[] { "Card Wars Kingdom", "Foundation v1.9.0.5 [Repack]", "TB" },
                         found.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n).ToArray());
            Assert.Equal(1062090, found.Single(f => f.Path == tb).SteamAppId);
            Assert.Equal("Foundation", found.Single(f => f.Path == foundation).Names.First());

            using var pc = new TestPc("P", Path.GetTempPath(), Manifest);
            foreach (var f in found)
                pc.Env.Installs.Add(new InstallCandidate { Path = f.Path, DisplayName = f.Names.FirstOrDefault(), SteamAppId = f.SteamAppId, Hints = f.Names, IsGameFolder = true });
            var scan = pc.Rescan();

            Assert.Equal(new[] { foundation }, scan.InstallPaths["Foundation"]);
            Assert.Equal("<base>/Saves/town.sav", scan.Games.Single(g => g.Name == "Foundation").Files.Single().Template);
            Assert.Equal(new[] { tb }, scan.InstallPaths["Timberborn"]);
            Assert.Contains(scan.InstalledWithoutSaves, g => g.Name == "Timberborn");
            Assert.Equal(unknown, scan.UnknownGameFolders.Single().Path);
        }

        [Theory]
        [InlineData("Foundation v1.9.0.5 [FitGirl Repack]", "Foundation")]
        [InlineData("Hollow_Knight", "Hollow Knight")]
        [InlineData("Timberborn.Update.6-GOG", "Timberborn")]
        [InlineData("The Witcher 3 (Build 12345)", "The Witcher 3")]
        [InlineData("Persona 5 Royal", "Persona 5 Royal")]
        public void Clean_RemovesReleaseNoise(string folder, string expected)
            => Assert.Equal(expected, GameFolderFinder.Clean(folder));
    }
}
