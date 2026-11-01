using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SaveCloud.Core.Manifest
{
    /// <summary>
    /// Downloads the Ludusavi manifest (save locations for 10k+ PC games, sourced from PCGamingWiki)
    /// and keeps a compact Windows-only cache of it.
    /// </summary>
    public static class ManifestLoader
    {
        public const string ManifestUrl = "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml";
        private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(3);
        /// <summary>Bumped when the cached fields change; an older cache is refreshed but still used if offline.</summary>
        private const int CurrentFormat = 2;

        private class CacheFile
        {
            public int Format { get; set; } = CurrentFormat;
            public string ETag { get; set; }
            public DateTime CheckedUtc { get; set; }
            public List<ManifestGame> Games { get; set; }
        }

        /// <summary>
        /// Returns the manifest, refreshing it from GitHub when the cache is stale. Falls back to the cache when offline.
        /// Returns an empty list only if there is no cache and the download fails.
        /// </summary>
        public static async Task<List<ManifestGame>> LoadAsync(CancellationToken ct = default)
        {
            CacheFile cache = ReadCache();
            if (cache != null && cache.Format == CurrentFormat && DateTime.UtcNow - cache.CheckedUtc < RefreshAfter)
                return cache.Games;

            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
                using (var req = new HttpRequestMessage(HttpMethod.Get, ManifestUrl))
                {
                    req.Headers.UserAgent.ParseAdd("SaveCloud/2.0");
                    if (cache?.ETag != null && cache.Format == CurrentFormat) req.Headers.TryAddWithoutValidation("If-None-Match", cache.ETag);

                    using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        if (resp.StatusCode == HttpStatusCode.NotModified && cache != null)
                        {
                            cache.CheckedUtc = DateTime.UtcNow;
                            JsonFile.WriteAtomic(AppPaths.ManifestCache, cache);
                            Logger.Info("Game manifest is up to date");
                            return cache.Games;
                        }
                        resp.EnsureSuccessStatusCode();

                        string tmp = AppPaths.ManifestYaml + ".download";
                        using (var fs = File.Create(tmp))
                            await resp.Content.CopyToAsync(fs).ConfigureAwait(false);

                        List<ManifestGame> games;
                        using (var reader = new StreamReader(tmp))
                            games = Parse(reader);
                        File.Delete(tmp);

                        var fresh = new CacheFile
                        {
                            ETag = resp.Headers.ETag?.ToString(),
                            CheckedUtc = DateTime.UtcNow,
                            Games = games
                        };
                        JsonFile.WriteAtomic(AppPaths.ManifestCache, fresh);
                        Logger.Info($"Downloaded game manifest: {games.Count} games with Windows save locations");
                        return games;
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                if (cache != null)
                {
                    Logger.Warn($"Could not refresh game manifest ({ex.Message}); using cached copy");
                    return cache.Games;
                }
                Logger.Error("Could not download the game manifest and no cached copy exists", ex);
                return new List<ManifestGame>();
            }
        }

        private static CacheFile ReadCache()
        {
            try
            {
                if (!File.Exists(AppPaths.ManifestCache)) return null;
                var cache = JsonConvert.DeserializeObject<CacheFile>(File.ReadAllText(AppPaths.ManifestCache));
                return cache?.Games != null ? cache : null;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Ignoring unreadable manifest cache: {ex.Message}");
                return null;
            }
        }

        // ---- YAML parsing -------------------------------------------------------------------------------

        private class RawGame
        {
            public Dictionary<string, RawFile> Files { get; set; }
            public Dictionary<string, object> InstallDir { get; set; }
            public RawSteam Steam { get; set; }
            public Dictionary<string, object> Cloud { get; set; }
        }

        private class RawFile
        {
            public List<string> Tags { get; set; }
            public List<RawWhen> When { get; set; }
        }

        private class RawWhen
        {
            public string Os { get; set; }
            public string Store { get; set; }
        }

        private class RawSteam
        {
            public long? Id { get; set; }
        }

        /// <summary>Parses the manifest YAML and keeps only games that have Windows-applicable save/config paths.</summary>
        public static List<ManifestGame> Parse(TextReader yaml)
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            var raw = deserializer.Deserialize<Dictionary<string, RawGame>>(yaml) ?? new Dictionary<string, RawGame>();
            var result = new List<ManifestGame>();

            foreach (var kv in raw)
            {
                var g = kv.Value;
                if (g?.Files == null || g.Files.Count == 0) continue;

                var game = new ManifestGame
                {
                    Name = kv.Key,
                    InstallDirs = g.InstallDir?.Keys.ToList() ?? new List<string>(),
                    SteamId = g.Steam?.Id,
                    SteamCloud = g.Cloud != null && g.Cloud.TryGetValue("steam", out var sc) && IsTrue(sc),
                    CloudStores = g.Cloud?.Where(c => IsTrue(c.Value)).Select(c => c.Key).ToList() ?? new List<string>(),
                };

                foreach (var f in g.Files)
                {
                    if (!AppliesToWindows(f.Value)) continue;
                    if (f.Key.IndexOf("<xdg", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    var tags = f.Value?.Tags;
                    bool untagged = tags == null || tags.Count == 0;
                    var rule = new FileRule
                    {
                        Pattern = f.Key,
                        IsSave = untagged || tags.Contains("save"),
                        IsConfig = tags != null && tags.Contains("config"),
                    };
                    if (rule.IsSave || rule.IsConfig) game.Files.Add(rule);
                }

                if (game.Files.Count > 0) result.Add(game);
            }
            return result;
        }

        private static bool AppliesToWindows(RawFile f)
        {
            if (f?.When == null || f.When.Count == 0) return true;
            return f.When.Any(w => w == null || string.IsNullOrEmpty(w.Os) || w.Os.Equals("windows", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsTrue(object o) => o is bool b ? b : string.Equals(o?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
    }
}
