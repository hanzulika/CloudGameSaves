using SaveCloud.Core.Manifest;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    public class ScanResult
    {
        public List<DetectedGame> Games { get; } = new List<DetectedGame>();
        /// <summary>Every manifest game's install folders on this PC, including games with no saves yet.</summary>
        public Dictionary<string, List<string>> InstallPaths { get; } = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        public List<string> Skipped { get; } = new List<string>();
    }

    /// <summary>Finds installed games and their save files using the Ludusavi manifest.</summary>
    public class GameScanner
    {
        private readonly PathEnvironment _env;
        private readonly AppConfig _config;
        private readonly PathTemplate _paths;
        /// <summary>From the last full scan: file path to the game that owns it (see <see cref="ResolveSharedFiles"/>).</summary>
        private Dictionary<string, string> _owners;

        public GameScanner(PathEnvironment env, AppConfig config)
        {
            _env = env;
            _config = config;
            _paths = new PathTemplate(env);
        }

        public PathTemplate Paths => _paths;

        /// <summary>Scans every game in the manifest. Takes a few seconds; run it off the UI thread.</summary>
        public ScanResult ScanAll(IList<ManifestGame> manifest, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var result = new ScanResult();
            var installs = FindInstallFolders(manifest);
            foreach (var kv in installs) result.InstallPaths[kv.Key] = kv.Value;

            var ignored = new HashSet<string>(_config.IgnoredGames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (var game in manifest)
            {
                ct.ThrowIfCancellationRequested();
                if (ignored.Contains(game.Name)) continue;

                installs.TryGetValue(game.Name, out var basePaths);
                var detected = ScanGame(game, basePaths ?? new List<string>());
                if (detected == null) continue;

                if (_config.SkipSteamCloudGames && game.SteamCloud && game.SteamId.HasValue
                    && _env.SteamInstalledAppIds.Contains(game.SteamId.Value))
                {
                    result.Skipped.Add($"{game.Name}: installed through Steam with Steam Cloud");
                    continue;
                }

                long maxBytes = (long)_config.MaxGameSizeMB * 1024 * 1024;
                if (detected.TotalBytes > maxBytes)
                {
                    result.Skipped.Add($"{game.Name}: saves are {detected.TotalBytes / (1024 * 1024)} MB (limit {_config.MaxGameSizeMB} MB)");
                    continue;
                }
                result.Games.Add(detected);
            }

            _owners = ResolveSharedFiles(result.Games);
            result.Games.RemoveAll(g => g.Files.Count == 0);

            Logger.Info($"Scan finished in {sw.ElapsedMilliseconds} ms: {result.Games.Count} games with saves, {result.Skipped.Count} skipped");
            return result;
        }

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
                    if (!owner.TryGetValue(f.ActualPath, out var cur) || f.PatternSpecificity > cur.spec
                        || (f.PatternSpecificity == cur.spec && g.InstallPaths.Count > cur.game.InstallPaths.Count))
                        owner[f.ActualPath] = (g, f.PatternSpecificity);

            foreach (var g in games)
                g.Files.RemoveAll(f => owner[f.ActualPath].game != g);

            var map = new Dictionary<string, string>(_env.Comparer);
            foreach (var kv in owner) map[kv.Key] = kv.Value.game.Name;
            return map;
        }

        /// <summary>Maps manifest game name to its install folders found under the configured/auto-detected roots.</summary>
        public Dictionary<string, List<string>> FindInstallFolders(IList<ManifestGame> manifest)
        {
            var byDir = new Dictionary<string, List<ManifestGame>>(_env.Comparer);
            foreach (var g in manifest)
            {
                var dirs = g.InstallDirs.Count > 0 ? g.InstallDirs : new List<string> { g.Name };
                foreach (var d in dirs)
                {
                    if (!byDir.TryGetValue(d, out var list)) byDir[d] = list = new List<ManifestGame>();
                    list.Add(g);
                }
            }

            var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
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
                {
                    if (!byDir.TryGetValue(Path.GetFileName(sub), out var games)) continue;
                    foreach (var g in games)
                    {
                        if (!found.TryGetValue(g.Name, out var list)) found[g.Name] = list = new List<string>();
                        if (!list.Contains(sub, _env.Comparer)) list.Add(sub);
                    }
                }
            }
            return found;
        }
    }
}
