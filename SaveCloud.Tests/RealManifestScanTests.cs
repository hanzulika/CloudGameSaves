using SaveCloud.Core.Manifest;
using SaveCloud.Core.Scan;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace SaveCloud.Tests
{
    /// <summary>
    /// Scans a simulated PC with the real Ludusavi manifest (when SAVECLOUD_MANIFEST points at a copy), using save
    /// folders reported from a real laptop.
    /// </summary>
    public class RealManifestScanTests
    {
        private readonly ITestOutputHelper _out;
        public RealManifestScanTests(ITestOutputHelper o) => _out = o;

        [Fact]
        public void FindsCommonGamesFromTheirStandardSaveFolders()
        {
            string path = Environment.GetEnvironmentVariable("SAVECLOUD_MANIFEST");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            List<ManifestGame> manifest;
            using (var r = new StreamReader(path)) manifest = ManifestLoader.Parse(r);

            using var pc = new TestPc("Real", Path.GetTempPath(), new List<ManifestGame>());
            pc.Write("winDocuments", "Electronic Arts/The Sims 4/saves/Slot_00000001.save", "sims");
            pc.Write("winDocuments", "Electronic Arts/The Sims 4/Mods/big.package", "mod");
            pc.Write("winDocuments", "Polymorph Games/Foundation/Save Game/town.sav", "foundation");
            pc.Write("winAppData", ".minecraft/saves/My World/level.dat", "mc");
            pc.Write("winDocuments", "The Witcher 3/gamesaves/AutoSave_1.sav", "w3");
            pc.Env.IgnoreCase = false;

            var scan = new GameScanner(pc.Env, pc.Config).ScanAll(manifest);
            foreach (var g in scan.Games) _out.WriteLine($"{g.Name}: {string.Join(", ", g.Files.Select(f => f.Template))}");
            foreach (var s in scan.Skipped) _out.WriteLine("skipped " + s);

            Assert.Contains(scan.Games, g => g.Name == "The Sims 4");
            Assert.Contains(scan.Games, g => g.Name == "Foundation");
            Assert.Contains(scan.Games, g => g.Name == "Minecraft: Java Edition");
            Assert.Contains(scan.Games, g => g.Name == "The Witcher 3: Wild Hunt");
        }
    }
}
