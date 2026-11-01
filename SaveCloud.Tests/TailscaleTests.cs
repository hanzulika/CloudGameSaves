using SaveCloud.Core.Remote;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace SaveCloud.Tests
{
    public class TailscaleTests
    {
        // Trimmed real-shape output of `tailscale status --json`.
        private const string StatusJson = @"{
  ""Version"": ""1.104.4"",
  ""BackendState"": ""Running"",
  ""Self"": { ""HostName"": ""hanzulaptop"", ""DNSName"": ""hanzulaptop.tail14f384.ts.net."", ""TailscaleIPs"": [""100.80.1.2""], ""UserID"": 111, ""Online"": true },
  ""MagicDNSSuffix"": ""tail14f384.ts.net"",
  ""Peer"": {
    ""nodekey:aa"": { ""HostName"": ""minecraft"", ""DNSName"": ""minecraft.tail14f384.ts.net."", ""TailscaleIPs"": [""100.90.0.5"", ""fd7a:115c:a1e0::5""], ""Online"": true, ""UserID"": 111 },
    ""nodekey:bb"": { ""HostName"": ""savecloud"", ""DNSName"": ""savecloud.tail14f384.ts.net."", ""TailscaleIPs"": [""100.99.0.9"", ""fd7a:115c:a1e0::9""], ""Online"": true, ""UserID"": 111 },
    ""nodekey:cc"": { ""HostName"": ""old-pc"", ""DNSName"": """", ""TailscaleIPs"": [""100.70.0.1""], ""Online"": false, ""UserID"": 111 }
  },
  ""User"": { ""111"": { ""ID"": 111, ""LoginName"": ""hanzu@example.com"", ""DisplayName"": ""Hanzu"" } }
}";

        [Fact]
        public void Parse_ReadsStateLoginAndPeers()
        {
            var s = TailscaleStatus.Parse(StatusJson);
            Assert.Equal(TailscaleState.Running, s.State);
            Assert.Equal("hanzu@example.com", s.Login);
            Assert.Equal(3, s.Peers.Count);
            Assert.Equal("100.99.0.9", s.Peers.Find(p => p.HostName == "savecloud").Ipv4);
        }

        [Theory]
        [InlineData("NeedsLogin", TailscaleState.NeedsLogin)]
        [InlineData("NeedsMachineAuth", TailscaleState.NeedsLogin)]
        [InlineData("Stopped", TailscaleState.Stopped)]
        [InlineData("Starting", TailscaleState.NotRunning)]
        public void Parse_States(string backend, TailscaleState expected)
            => Assert.Equal(expected, TailscaleStatus.Parse($"{{\"BackendState\":\"{backend}\"}}").State);

        [Fact]
        public void Candidates_PreferOverrideThenLastThenSavecloudNamedThenOnlinePeers()
        {
            var urls = ServerLocator.Candidates(TailscaleStatus.Parse(StatusJson), "", "http://100.1.1.1:8765");
            Assert.Equal(new[] { "http://100.1.1.1:8765", "http://100.99.0.9:8765", "http://100.90.0.5:8765" }, urls);
        }

        [Fact]
        public async Task Find_PrefersAnAuthorizedServer()
        {
            var answers = new Dictionary<string, ServerInfo>
            {
                ["http://a"] = new ServerInfo { Service = "savecloud", Api = 1, Authorized = false, Login = "me" },
                ["http://c"] = new ServerInfo { Service = "savecloud", Api = 1, Authorized = true, Login = "me" },
            };
            Task<ServerInfo> Probe(string url, TimeSpan t) => Task.FromResult(answers.TryGetValue(url, out var i) ? i : null);

            var found = await ServerLocator.FindAsync(new[] { "http://a", "http://b", "http://c", "http://d" }, Probe);
            Assert.Equal("http://c", found.Url);

            answers.Remove("http://c");
            found = await ServerLocator.FindAsync(new[] { "http://a", "http://b" }, Probe);
            Assert.Equal("http://a", found.Url);
            Assert.False(found.Info.Authorized);

            Assert.Null(await ServerLocator.FindAsync(new[] { "http://x" }, Probe));
        }

        [Theory]
        [InlineData("Pokémon Sword")]
        [InlineData(".hack//G.U. Last Recode")]
        [InlineData("ファイナルファンタジー")]
        [InlineData("...")]
        public void Slugs_AreServerSafe(string name)
        {
            string slug = Slugs.Of(name);
            Assert.Matches("^[A-Za-z0-9_.()\\-]{1,120}$", slug);
            Assert.False(slug.StartsWith("."));
            Assert.NotEqual(Slugs.Of(name + " "), slug);
        }
    }
}
