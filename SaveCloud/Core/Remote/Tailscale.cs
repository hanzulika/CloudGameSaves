using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SaveCloud.Core.Remote
{
    public enum TailscaleState
    {
        NotInstalled,
        /// <summary>Installed, but the Tailscale service isn't running.</summary>
        NotRunning,
        /// <summary>Running, but signed out / needs approval.</summary>
        NeedsLogin,
        /// <summary>Signed in but switched off ("Disconnected" in the tray).</summary>
        Stopped,
        Running,
    }

    public class TailscalePeer
    {
        public string HostName { get; set; }
        public string DnsName { get; set; }
        public List<string> Addresses { get; set; } = new List<string>();
        public bool Online { get; set; }
        public string Ipv4 => Addresses.FirstOrDefault(a => a.IndexOf('.') > 0);
    }

    public class TailscaleStatus
    {
        public TailscaleState State { get; set; }
        public string Login { get; set; }
        public List<TailscalePeer> Peers { get; set; } = new List<TailscalePeer>();
        public string Error { get; set; }

        /// <summary>Parses the output of <c>tailscale status --json</c>.</summary>
        public static TailscaleStatus Parse(string json)
        {
            var o = JObject.Parse(json);
            var s = new TailscaleStatus();
            switch ((string)o["BackendState"])
            {
                case "Running": s.State = TailscaleState.Running; break;
                case "Stopped": s.State = TailscaleState.Stopped; break;
                case "NoState":
                case "Starting": s.State = TailscaleState.NotRunning; break;
                default: s.State = TailscaleState.NeedsLogin; break; // NeedsLogin, NeedsMachineAuth
            }

            string selfUser = o["Self"]?["UserID"]?.ToString();
            if (selfUser != null) s.Login = (string)o["User"]?[selfUser]?["LoginName"];

            if (o["Peer"] is JObject peers)
            {
                foreach (var p in peers.Properties().Select(x => x.Value))
                {
                    s.Peers.Add(new TailscalePeer
                    {
                        HostName = (string)p["HostName"] ?? "",
                        DnsName = ((string)p["DNSName"] ?? "").TrimEnd('.'),
                        Addresses = p["TailscaleIPs"]?.Select(x => (string)x).ToList() ?? new List<string>(),
                        Online = (bool?)p["Online"] ?? false,
                    });
                }
            }
            return s;
        }
    }

    /// <summary>Finds the SaveCloud server among this PC's Tailscale peers. No address needs to be typed in.</summary>
    public static class ServerLocator
    {
        public const int Port = 8765;

        public class Found
        {
            public string Url { get; set; }
            public ServerInfo Info { get; set; }
        }

        /// <summary>
        /// Candidate URLs, best first: a manual override, the last server that worked, peers named like "savecloud",
        /// then every other online peer. The 100.x IP is used rather than the MagicDNS name because MagicDNS names
        /// don't resolve for devices shared from someone else's tailnet.
        /// </summary>
        public static List<string> Candidates(TailscaleStatus ts, string overrideUrl, string lastUrl)
        {
            var urls = new List<string>();
            void Add(string u)
            {
                if (!string.IsNullOrWhiteSpace(u) && !urls.Contains(u, StringComparer.OrdinalIgnoreCase)) urls.Add(u.TrimEnd('/'));
            }

            Add(overrideUrl);
            Add(lastUrl);
            if (ts?.Peers != null)
            {
                bool Named(TailscalePeer p) =>
                    p.HostName.StartsWith("savecloud", StringComparison.OrdinalIgnoreCase) ||
                    p.DnsName.StartsWith("savecloud", StringComparison.OrdinalIgnoreCase);

                foreach (var p in ts.Peers.Where(Named).OrderByDescending(p => p.Online))
                    if (p.Ipv4 != null) Add($"http://{p.Ipv4}:{Port}");
                foreach (var p in ts.Peers.Where(p => p.Online && !Named(p)).Take(40))
                    if (p.Ipv4 != null) Add($"http://{p.Ipv4}:{Port}");
            }
            return urls;
        }

        /// <summary>
        /// Probes candidates (likely ones first, the rest in parallel) and returns the first SaveCloud server, preferring
        /// one that has authorized this login. Null if none answers.
        /// </summary>
        public static async Task<Found> FindAsync(IList<string> candidates, Func<string, TimeSpan, Task<ServerInfo>> probe = null)
        {
            probe = probe ?? HttpStore.ProbeAsync;
            var timeout = TimeSpan.FromSeconds(3);
            Found unauthorized = null;

            // The first two (override, last known) are tried on their own; usually one of them is it.
            foreach (var url in candidates.Take(2))
            {
                var info = await probe(url, timeout).ConfigureAwait(false);
                if (info == null) continue;
                if (info.Authorized) return new Found { Url = url, Info = info };
                unauthorized = unauthorized ?? new Found { Url = url, Info = info };
            }

            var rest = candidates.Skip(2).ToList();
            var tasks = rest.Select(async u => new Found { Url = u, Info = await probe(u, timeout).ConfigureAwait(false) }).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results.FirstOrDefault(r => r.Info?.Authorized == true)
                   ?? unauthorized
                   ?? results.FirstOrDefault(r => r.Info != null);
        }
    }
}
