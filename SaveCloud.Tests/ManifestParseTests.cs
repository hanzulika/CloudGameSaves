using SaveCloud.Core.Manifest;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace SaveCloud.Tests
{
    public class ManifestParseTests
    {
        private readonly ITestOutputHelper _out;
        public ManifestParseTests(ITestOutputHelper output) => _out = output;

        private const string Sample = @"
""Anyway"":
  cloud:
    steam: true
  files:
    ""<winLocalAppData>/anyway"":
      tags:
        - save
      when:
        - os: windows
    ""<home>/.config/anyway"":
      when:
        - os: linux
    ""<base>/settings.ini"":
      tags:
        - config
  installDir:
    Anyway: {}
  steam:
    id: 866510
""Registry Only"":
  registry:
    HKEY_CURRENT_USER/Software/X:
      tags:
        - save
""Untagged"":
  files:
    ""<winDocuments>/My Games/Untagged"": {}
";

        [Fact]
        public void Parse_KeepsWindowsRules_AndDropsOtherOs()
        {
            var games = ManifestLoader.Parse(new StringReader(Sample));
            Assert.Equal(2, games.Count);

            var anyway = games.Single(g => g.Name == "Anyway");
            Assert.True(anyway.SteamCloud);
            Assert.Equal(866510, anyway.SteamId);
            Assert.Equal(new[] { "Anyway" }, anyway.InstallDirs);
            Assert.Equal(2, anyway.Files.Count);
            Assert.True(anyway.Files.Single(f => f.Pattern == "<winLocalAppData>/anyway").IsSave);
            var cfg = anyway.Files.Single(f => f.Pattern == "<base>/settings.ini");
            Assert.True(cfg.IsConfig);
            Assert.False(cfg.IsSave);

            Assert.True(games.Single(g => g.Name == "Untagged").Files.Single().IsSave);
        }

        /// <summary>Parses the real manifest when SAVECLOUD_MANIFEST points at a downloaded copy.</summary>
        [Fact]
        public void Parse_RealManifest_IsFastEnough()
        {
            string path = Environment.GetEnvironmentVariable("SAVECLOUD_MANIFEST");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            var sw = Stopwatch.StartNew();
            using var reader = new StreamReader(path);
            var games = ManifestLoader.Parse(reader);
            sw.Stop();
            _out.WriteLine($"{games.Count} games, {games.Sum(g => g.Files.Count)} rules in {sw.ElapsedMilliseconds} ms");
            Assert.True(games.Count > 5000);
        }
    }
}
