using SaveCloud.Core.Manifest;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SaveCloud.Core.Scan
{
    /// <summary>A game that has save files on this PC.</summary>
    public class DetectedGame
    {
        public ManifestGame Manifest { get; set; }
        public string Name => Manifest.Name;
        /// <summary>Install folders found on this PC (empty when only the saves were found).</summary>
        public List<string> InstallPaths { get; set; } = new List<string>();
        public List<SaveFile> Files { get; set; } = new List<SaveFile>();
        public long TotalBytes => Files.Sum(f => f.Size);
    }

    public enum SkipReason { LauncherCloud, TooLarge }

    public class SkippedGame
    {
        public string Name { get; set; }
        public SkipReason Reason { get; set; }
        /// <summary>For <see cref="SkipReason.LauncherCloud"/>: steam, epic, gog, origin or uplay.</summary>
        public string Store { get; set; }
        public long? SteamId { get; set; }
        public int Files { get; set; }
        public long Bytes { get; set; }
        /// <summary>Folders holding its saves and install, so they aren't suggested as unknown saves.</summary>
        public List<string> Folders { get; set; } = new List<string>();
    }

    public class ScanResult
    {
        public List<DetectedGame> Games { get; } = new List<DetectedGame>();
        /// <summary>Every manifest game's install folders on this PC, including games with no saves yet.</summary>
        public Dictionary<string, List<string>> InstallPaths { get; } = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        public List<string> Skipped { get; } = new List<string>();
        /// <summary>Games found on this PC but not synced, with the reason (shown in the UI, not hidden).</summary>
        public List<SkippedGame> SkippedGames { get; } = new List<SkippedGame>();
        /// <summary>Folders that look like game saves but belong to no known game (offered to the user to add).</summary>
        public List<SaveSuggestion> Suggestions { get; } = new List<SaveSuggestion>();
    }

    /// <summary>Finds installed games and their save files using the Ludusavi manifest plus the user's own games.</summary>
    public class GameScanner
    {
        private readonly PathEnvironment _env;
        private readonly AppConfig _config;
        private readonly PathTemplate _paths;
        /// <summary>From the last full scan: file path to the game that owns it (see <see cref="ResolveSharedFiles"/>).</summary>
        private Dictionary<string, string> _owners;
        /// <summary>From the last full scan: install folder to the launcher it was installed with.</summary>
        private Dictionary<string, string> _storeByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public GameScanner(PathEnvironment env, AppConfig config)
        {
            _env = env;
            _config = config;
            _paths = new PathTemplate(env);
        }

        public PathTemplate Paths => _paths;
        public PathEnvironment Env => _env;

        /// <summary>The manifest plus the user's custom games (a custom game with a manifest name adds folders to it).</summary>
        public static List<ManifestGame> WithCustomGames(IList<ManifestGame> manifest, IEnumerable<CustomGame> custom)
        {
            var result = manifest.ToList();
            var byName = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < result.Count; i++) byName[result[i].Name] = i;

            foreach (var c in custom ?? Enumerable.Empty<CustomGame>())
            {
                if (string.IsNullOrWhiteSpace(c?.Name) || string.IsNullOrWhiteSpace(c.Pattern)) continue;
                var rule = new FileRule { Pattern = c.Pattern, IsSave = true };
                if (byName.TryGetValue(c.Name, out int i))
                {
                    var g = result[i];
                    result[i] = new ManifestGame
                    {
                        Name = g.Name, InstallDirs = g.InstallDirs, SteamId = g.SteamId, SteamCloud = g.SteamCloud,
                        CloudStores = g.CloudStores, Custom = true,
                        Files = g.Files.Concat(new[] { rule }).ToList(),
                    };
                }
                else
                {
                    byName[c.Name] = result.Count;
                    result.Add(new ManifestGame { Name = c.Name, Custom = true, Files = { rule } });
                }
            }
            return result;
        }

        /// <summary>Scans every game in the manifest. Takes a few seconds; run it off the UI thread.</summary>
        public ScanResult ScanAll(IList<ManifestGame> manifest, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var result = new ScanResult();
            var installs = FindInstallFolders(manifest, out _storeByPath);
            foreach (var kv in installs) result.InstallPaths[kv.Key] = kv.Value;

            var ignored = new HashSet<string>(_config.IgnoredGames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var forced = new HashSet<string>(_config.AlsoSyncCloudGames ?? new List<string>(), StringComparer.Ordinal);
            long maxBytes = (long)_config.MaxGameSizeMB * 1024 * 1024;

            foreach (var game in manifest)
            {
                ct.ThrowIfCancellationRequested();
                if (ignored.Contains(game.Name)) continue;

                installs.TryGetValue(game.Name, out var basePaths);
                var detected = ScanGame(game, basePaths ?? new List<string>());
                if (detected == null) continue;

                string store = LauncherCloudStore(game, basePaths);
                if (_config.SkipLauncherCloudGames && store != null && !game.Custom && !forced.Contains(game.Name))
                {
                    result.Skipped.Add($"{game.Name}: installed through {store}, which syncs its saves");
                    result.SkippedGames.Add(Skip(detected, SkipReason.LauncherCloud, store));
                    continue;
                }

                if (detected.TotalBytes > maxBytes)
                {
                    result.Skipped.Add($"{game.Name}: saves are {detected.TotalBytes / (1024 * 1024)} MB (limit {_config.MaxGameSizeMB} MB)");
                    result.SkippedGames.Add(Skip(detected, SkipReason.TooLarge, null));
                    continue;
                }
                result.Games.Add(detected);
            }

            _owners = ResolveSharedFiles(result.Games);
            result.Games.RemoveAll(g => g.Files.Count == 0);

            try
            {
                result.Suggestions.AddRange(new SaveSuggester(_env, _config, _paths).Find(result, manifest));
            }
            catch (Exception ex)
            {
                Logger.Warn($"Looking for other save folders failed: {ex.Message}");
            }

            Logger.Info($"Scan finished in {sw.ElapsedMilliseconds} ms: {result.Games.Count} games with saves, " +
                        $"{result.Skipped.Count} skipped, {result.Suggestions.Count} possible save folders, " +
                        $"{installs.Count} installed games recognised");
            return result;
        }

        /// <summary>
        /// The launcher whose own cloud already syncs this game on this PC, or null. A game only counts when it was
        /// actually installed through that launcher: a GOG copy of a Steam Cloud game is still synced.
        /// </summary>
        private string LauncherCloudStore(ManifestGame game, List<string> basePaths)
        {
            if (game.CloudStores == null || game.CloudStores.Count == 0) return null;
            if (game.SteamId.HasValue && _env.SteamInstalledAppIds.Contains(game.SteamId.Value) && game.CloudStores.Contains("steam"))
                return "steam";
            foreach (var p in basePaths ?? new List<string>())
                if (_storeByPath.TryGetValue(p, out var store) && store != null && game.CloudStores.Contains(store))
                    return store;
            return null;
        }

        private static SkippedGame Skip(DetectedGame g, SkipReason why, string store) => new SkippedGame
        {
            Name = g.Name, Reason = why, Store = store, SteamId = g.Manifest.SteamId, Files = g.Files.Count, Bytes = g.TotalBytes,
            Folders = g.Files.Select(f => Path.GetDirectoryName(f.ActualPath)).Concat(g.InstallPaths).Distinct().ToList(),
        };

        /// <summary>Re-scans one game (after it closes, or before/after a restore).</summary>
        public DetectedGame ScanGame(ManifestGame game, IList<string> basePaths)
        {
            var names = new List<string>();
            names.AddRange(game.InstallDirs);
            if (!names.Contains(game.Name)) names.Add(game.Name);

            var files = new List<SaveFile>();
            foreach (var rule in game.Files)
            {
                if (!rule.IsSave && !(rule.IsConfig && _config.IncludeConfigFiles)) continue;
                bool needsBase = rule.Pattern.StartsWith("<base>", StringComparison.OrdinalIgnoreCase);
                if (needsBase && basePaths.Count == 0) continue;
                files.AddRange(_paths.Expand(rule.Pattern, basePaths, names, game.SteamId));
            }
            // Keep single-game rescans consistent with the last full scan: files owned by another game stay with it.
            var owners = _owners;
            if (owners != null)
                files.RemoveAll(f => owners.TryGetValue(f.ActualPath, out var o) && o != game.Name);
            if (files.Count == 0) return null;

            return new DetectedGame
            {
                Manifest = game,
                InstallPaths = basePaths.ToList(),
                Files = Dedupe(files),
            };
        }

        /// <summary>One entry per file; when two rules reach the same file, keep the most portable template.</summary>
        private List<SaveFile> Dedupe(List<SaveFile> files)
        {
            return files
                .GroupBy(f => f.ActualPath, _env.Comparer)
                .Select(g => g
                    .OrderBy(f => f.Template.StartsWith("<home>", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenByDescending(f => f.PatternSpecificity)
                    .First())
                .OrderBy(f => f.Template, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Two manifest entries can match the same file (sequels, editions, generic folders). If both games synced it,
        /// they'd overwrite each other. Give each file to the game whose rule is the most specific.
        /// </summary>
        private Dictionary<string, string> ResolveSharedFiles(List<DetectedGame> games)
        {
            var owner = new Dictionary<string, (DetectedGame game, int spec)>(_env.Comparer);
            foreach (var g in games)
                foreach (var f in g.Files)
                {
                    // A game the user added by hand always wins: they pointed at that folder on purpose.
                    int spec = f.PatternSpecificity + (g.Manifest.Custom ? 100000 : 0);
                    if (!owner.TryGetValue(f.ActualPath, out var cur) || spec > cur.spec
                        || (spec == cur.spec && g.InstallPaths.Count > cur.game.InstallPaths.Count))
                        owner[f.ActualPath] = (g, spec);
                }

            foreach (var g in games)
                g.Files.RemoveAll(f => owner[f.ActualPath].game != g);

            var map = new Dictionary<string, string>(_env.Comparer);
            foreach (var kv in owner) map[kv.Key] = kv.Value.game.Name;
            return map;
        }

        // ---- install folders --------------------------------------------------------------------------------

        /// <summary>Lowercase letters and digits only: "The Witcher® 3: Wild Hunt" -> "thewitcher3wildhunt".</summary>
        public static string NormalizeName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name.Replace("&", "and"))
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        public Dictionary<string, List<string>> FindInstallFolders(IList<ManifestGame> manifest)
            => FindInstallFolders(manifest, out _);

        /// <summary>
        /// Maps manifest game name to its install folders on this PC. Sources: game libraries (subfolder name =
        /// the game's install folder name), Windows' installed-programs list and shortcuts (folder name, or the
        /// program's display name matched against game names).
        /// </summary>
        public Dictionary<string, List<string>> FindInstallFolders(IList<ManifestGame> manifest, out Dictionary<string, string> storeByPath)
        {
            var byDir = new Dictionary<string, List<ManifestGame>>(_env.Comparer);
            var byName = new Dictionary<string, List<ManifestGame>>(StringComparer.Ordinal);
            foreach (var g in manifest)
            {
                var dirs = g.InstallDirs.Count > 0 ? g.InstallDirs : new List<string> { g.Name };
                foreach (var d in dirs) Add(byDir, d, g);
                string n = NormalizeName(g.Name);
                if (n.Length >= 3) Add(byName, n, g);
            }
            var sortedNames = byName.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

            var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            storeByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var stores = storeByPath;

            void Found(ManifestGame g, string path, string store)
            {
                if (!found.TryGetValue(g.Name, out var list)) found[g.Name] = list = new List<string>();
                if (!list.Contains(path, _env.Comparer)) list.Add(path);
                if (store != null && !stores.ContainsKey(path)) stores[path] = store;
            }

            foreach (var root in _env.Roots)
            {
                string[] subdirs;
                try
                {
                    if (!Directory.Exists(root.GamesFolder)) continue;
                    subdirs = Directory.GetDirectories(root.GamesFolder);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var sub in subdirs)
                    if (byDir.TryGetValue(Path.GetFileName(sub), out var games))
                        foreach (var g in games) Found(g, sub, root.Store);
            }

            foreach (var c in _env.Installs)
            {
                if (string.IsNullOrEmpty(c.Path)) continue;
                string dir = c.Path.TrimEnd('\\', '/');
                bool matched = false;
                if (byDir.TryGetValue(Path.GetFileName(dir), out var games))
                {
                    foreach (var g in games) Found(g, dir, c.Store);
                    matched = true;
                }
                if (!matched && !string.IsNullOrEmpty(c.DisplayName))
                    foreach (var g in MatchByName(c.DisplayName, byName, sortedNames)) Found(g, dir, c.Store);
            }
            return found;
        }

        /// <summary>
        /// Games whose name matches a program/shortcut name: exactly, or the program name starts with the game name
        /// ("The Sims 4 DX9" -> "The Sims 4"), or it's an unambiguous start of one ("The Witcher 3" -> "...: Wild Hunt").
        /// </summary>
        private static IEnumerable<ManifestGame> MatchByName(string displayName, Dictionary<string, List<ManifestGame>> byName, string[] sorted)
        {
            string n = NormalizeName(displayName);
            if (n.Length < 4) return Enumerable.Empty<ManifestGame>();
            if (byName.TryGetValue(n, out var exact)) return exact;

            // Longest game name that the display name starts with (at least 6 letters, to avoid "steam" -> "Steam...").
            for (int len = n.Length - 1; len >= 6; len--)
                if (byName.TryGetValue(n.Substring(0, len), out var prefix)) return prefix;

            // Display name is the start of exactly one game name.
            if (n.Length >= 8)
            {
                int i = Array.BinarySearch(sorted, n, StringComparer.Ordinal);
                if (i < 0) i = ~i;
                var hits = new List<string>();
                for (; i < sorted.Length && sorted[i].StartsWith(n, StringComparison.Ordinal) && hits.Count < 2; i++) hits.Add(sorted[i]);
                if (hits.Count == 1) return byName[hits[0]];
            }
            return Enumerable.Empty<ManifestGame>();
        }

        private static void Add<TK>(Dictionary<TK, List<ManifestGame>> d, TK key, ManifestGame g)
        {
            if (!d.TryGetValue(key, out var list)) d[key] = list = new List<ManifestGame>();
            list.Add(g);
        }
    }

    /// <summary>A save folder the user added by hand (or accepted from the suggestions).</summary>
    public class CustomGame
    {
        public string Name { get; set; }
        /// <summary>Portable folder template, e.g. "&lt;winAppData&gt;/shadPS4/user/savedata".</summary>
        public string Pattern { get; set; }
    }
}
