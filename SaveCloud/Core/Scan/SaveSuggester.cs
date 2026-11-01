using SaveCloud.Core.Manifest;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SaveCloud.Core.Scan
{
    /// <summary>A folder that looks like game saves but isn't claimed by any known game.</summary>
    public class SaveSuggestion
    {
        public string Name { get; set; }
        public string Folder { get; set; }
        public string Template { get; set; }
        public int Files { get; set; }
        public long Bytes { get; set; }
        public DateTime NewestUtc { get; set; }
        public string Source { get; set; }
    }

    /// <summary>
    /// Finds save folders the manifest doesn't know: non-store or repacked copies that save elsewhere, emulators,
    /// small indie games. Looks in the places games conventionally save (Documents, My Games, Saved Games, AppData,
    /// Unity's LocalLow, Unreal's Saved\SaveGames) and inside installed programs' folders. Only folders with clearly
    /// save-like names or files qualify, and the user confirms each one, so nothing is synced by guesswork.
    /// </summary>
    internal class SaveSuggester
    {
        private const int MaxSuggestions = 60;
        private const int MaxEntriesPerProbe = 600;

        private static readonly Regex SaveDirName = new Regex(
            @"^(auto)?save(s|game|games|data|_data|files?|states?|slots?)?$|^save[ _-]?(games?|data|files?)$|^game[ _-]?saves?$|^saved[ _-]?games$|^savegames?$|^userdata[ _-]?saves?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HashSet<string> SaveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".sav", ".save", ".savegame", ".sl2", ".ess", ".rpgsave", ".gamesave", ".fos", ".lsv", ".sav2" };

        /// <summary>Vendors and apps whose folders are never game saves.</summary>
        private static readonly HashSet<string> NotGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Windows", "Google", "Mozilla", "Adobe", "NVIDIA", "NVIDIA Corporation", "AMD", "ATI", "Intel",
            "Apple", "Oracle", "Sun", "Java", "Packages", "Temp", "CrashDumps", "Discord", "Spotify", "Zoom",
            "Steam", "Epic Games", "EpicGamesLauncher", "UnrealEngine", "GOG.com", "GalaxyClient", "Origin", "EA Desktop",
            "Electronic Arts\\EA Desktop", "Ubisoft Game Launcher", "Unity", "Unity Technologies", "Microsoft Games",
            "Visual Studio", "Code", "JetBrains", "npm", "pip", "NuGet", "Programs", "SaveCloud", "WinRAR", "7-Zip",
            "Custom Office Templates", "OneNote Notebooks", "Zoom", "WindowsPowerShell", "PowerShell", "Fax", "Scanned Documents",
            "My Music", "My Pictures", "My Videos", "desktop.ini", "Downloads", "Arduino", "ShareX", "calibre",
        };

        private readonly PathEnvironment _env;
        private readonly AppConfig _config;
        private readonly PathTemplate _paths;

        public SaveSuggester(PathEnvironment env, AppConfig config, PathTemplate paths)
        {
            _env = env;
            _config = config;
            _paths = paths;
        }

        public List<SaveSuggestion> Find(ScanResult scan, IList<ManifestGame> manifest)
        {
            var claimed = new List<string>();
            foreach (var g in scan.Games)
                foreach (var f in g.Files)
                    claimed.Add(Path.GetDirectoryName(f.ActualPath));
            foreach (var g in scan.Games) claimed.AddRange(g.InstallPaths);
            claimed.AddRange(scan.SkippedGames.SelectMany(s => s.Folders ?? new List<string>()));
            claimed = claimed.Distinct(_env.Comparer).ToList();

            var hidden = new HashSet<string>((_config.HiddenSuggestions ?? new List<string>())
                .Concat((_config.CustomGames ?? new List<CustomGame>()).Select(c => c.Pattern)), StringComparer.OrdinalIgnoreCase);

            var candidates = new List<(string folder, string name, string source)>();
            string docs = Folder("winDocuments"), home = Folder("home");

            if (docs != null)
            {
                foreach (var d in Dirs(docs))
                    if (!Path.GetFileName(d).Equals("My Games", StringComparison.OrdinalIgnoreCase)) PublisherOrGame(d, "Documents", candidates);
                foreach (var d in Dirs(Path.Combine(docs, "My Games"))) PublisherOrGame(d, "My Games", candidates);
            }
            if (home != null)
                foreach (var d in Dirs(Path.Combine(home, "Saved Games"))) PublisherOrGame(d, "Saved Games", candidates);

            var lowLow = Folder("winLocalAppDataLow");
            if (lowLow != null)
                foreach (var company in Dirs(lowLow))
                    foreach (var product in Dirs(company))
                        if (LooksLikeSaves(product, 3)) candidates.Add((SaveSubfolder(product) ?? product, Path.GetFileName(product), "AppData\\LocalLow"));

            var local = Folder("winLocalAppData");
            if (local != null)
                foreach (var d in Dirs(local))
                {
                    string unreal = Path.Combine(d, "Saved", "SaveGames");
                    if (Directory.Exists(unreal)) candidates.Add((unreal, Path.GetFileName(d), "AppData\\Local"));
                    else { var s = SaveSubfolder(d); if (s != null) candidates.Add((s, Path.GetFileName(d), "AppData\\Local")); }
                }

            var roaming = Folder("winAppData");
            if (roaming != null)
                foreach (var d in Dirs(roaming))
                {
                    var s = SaveSubfolder(d);
                    if (s != null) candidates.Add((s, Path.GetFileName(d), "AppData\\Roaming"));
                }

            // Installed programs and game-library folders: saves kept next to the game (repacks, emulators, old games).
            var installs = _env.Installs.Select(i => (i.Path, i.DisplayName, i.Source)).ToList();
            foreach (var root in _env.Roots.Where(r => r.Kind == RootKind.SteamLibrary || IsGamesFolder(r.Path)))
                foreach (var d in Dirs(root.GamesFolder)) installs.Add((d, Path.GetFileName(d), "game folder"));
            foreach (var (path, name, source) in installs)
            {
                if (string.IsNullOrEmpty(path) || IsSystemPath(path)) continue;
                var s = FindSaveDir(path, 3);
                if (s != null) candidates.Add((s, string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path) : name, source));
            }

            var results = new List<SaveSuggestion>();
            var seen = new HashSet<string>(_env.Comparer);
            long maxBytes = (long)_config.MaxGameSizeMB * 1024 * 1024;
            foreach (var (folder, name, source) in candidates)
            {
                if (!seen.Add(folder) || IsClaimed(folder, claimed) || NotGames.Contains(Path.GetFileName(folder))
                    || NotGames.Contains(name ?? "")) continue;
                string template = _paths.ToTemplate(folder);
                if (hidden.Contains(template)) continue;
                var (files, bytes, newest) = Measure(folder);
                if (files == 0 || bytes > maxBytes) continue;
                results.Add(new SaveSuggestion
                {
                    Name = CleanName(name),
                    Folder = folder,
                    Template = template,
                    Files = files,
                    Bytes = bytes,
                    NewestUtc = newest,
                    Source = source,
                });
            }
            return results.OrderByDescending(r => r.NewestUtc).Take(MaxSuggestions).ToList();
        }

        /// <summary>Documents\X: either a game folder, or a publisher folder whose subfolders are games.</summary>
        private void PublisherOrGame(string dir, string source, List<(string, string, string)> into)
        {
            if (NotGames.Contains(Path.GetFileName(dir))) return;
            var own = SaveSubfolder(dir);
            if (own != null || HasSaveFiles(dir))
            {
                into.Add((own ?? dir, Path.GetFileName(dir), source));
                return;
            }
            foreach (var game in Dirs(dir))
                if (LooksLikeSaves(game, 2)) into.Add((SaveSubfolder(game) ?? game, Path.GetFileName(game), source));
        }

        /// <summary>A direct subfolder with a save-like name ("saves", "SaveGames", "savedata", ...), if any.</summary>
        private static string SaveSubfolder(string dir)
            => Dirs(dir).FirstOrDefault(d => SaveDirName.IsMatch(Path.GetFileName(d)));

        /// <summary>Breadth-first search (bounded) for a save-named folder inside an install folder.</summary>
        private static string FindSaveDir(string root, int depth)
        {
            var level = new List<string> { root };
            int visited = 0;
            for (int i = 0; i < depth && level.Count > 0; i++)
            {
                var next = new List<string>();
                foreach (var d in level)
                    foreach (var sub in Dirs(d))
                    {
                        if (++visited > MaxEntriesPerProbe) return null;
                        if (SaveDirName.IsMatch(Path.GetFileName(sub))) return sub;
                        next.Add(sub);
                    }
                level = next;
            }
            return null;
        }

        private static bool LooksLikeSaves(string dir, int depth)
        {
            if (SaveSubfolder(dir) != null || HasSaveFiles(dir)) return true;
            if (depth <= 1) return false;
            int n = 0;
            foreach (var sub in Dirs(dir))
            {
                if (++n > 40) break;
                if (LooksLikeSaves(sub, depth - 1)) return true;
            }
            return false;
        }

        private static bool HasSaveFiles(string dir)
        {
            try
            {
                int n = 0;
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    if (++n > 200) break;
                    string name = Path.GetFileName(f);
                    if (SaveExtensions.Contains(Path.GetExtension(name)) ||
                        (name.IndexOf("save", StringComparison.OrdinalIgnoreCase) >= 0 && !name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            return false;
        }

        private bool IsClaimed(string folder, List<string> claimed)
        {
            foreach (var c in claimed)
            {
                if (c == null) continue;
                if (IsInside(c, folder) || IsInside(folder, c)) return true;
            }
            return false;
        }

        private bool IsInside(string path, string folder)
            => path.Equals(folder, _env.Comparison) ||
               (path.Length > folder.Length && path.StartsWith(folder, _env.Comparison) &&
                (path[folder.Length] == '\\' || path[folder.Length] == '/'));

        private (int files, long bytes, DateTime newest) Measure(string folder)
        {
            int files = 0;
            long bytes = 0;
            var newest = DateTime.MinValue;
            try
            {
                foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    files++;
                    bytes += f.Length;
                    if (f.LastWriteTimeUtc > newest) newest = f.LastWriteTimeUtc;
                    if (files > 20000) break;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            return (files, bytes, newest);
        }

        private bool IsGamesFolder(string path)
            => !string.Equals(path, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(path, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), StringComparison.OrdinalIgnoreCase);

        private bool IsSystemPath(string path)
        {
            string win = Folder("winDir");
            if (win != null && IsInside(path, win)) return true;
            return path.IndexOf("\\Common Files", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   NotGames.Any(n => path.IndexOf("\\" + n + "\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     path.EndsWith("\\" + n, StringComparison.OrdinalIgnoreCase));
        }

        private string Folder(string token) => _env.Folders.TryGetValue(token, out var f) && Directory.Exists(f) ? f : null;

        private static IEnumerable<string> Dirs(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return Enumerable.Empty<string>();
                return Directory.GetDirectories(dir).Where(d =>
                {
                    try { return (File.GetAttributes(d) & (FileAttributes.ReparsePoint | FileAttributes.System)) == 0; }
                    catch { return false; }
                }).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            return Enumerable.Empty<string>();
        }

        private static string CleanName(string name)
        {
            name = (name ?? "").Trim().TrimStart('.').Replace('_', ' ');
            return name.Length == 0 ? "Unknown game" : char.ToUpperInvariant(name[0]) + name.Substring(1);
        }
    }
}
