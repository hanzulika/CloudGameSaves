using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SaveCloud.Core.Scan
{
    /// <summary>A folder that contains an installed game, found by what's inside it rather than where it is.</summary>
    public class FoundGameFolder
    {
        public string Path { get; set; }
        /// <summary>Why it counts as a game ("Unity", "Unreal", "Steam API", ...).</summary>
        public string Signature { get; set; }
        /// <summary>Steam app id from steam_appid.txt, when the game ships one.</summary>
        public long? SteamAppId { get; set; }
        /// <summary>Candidate names, best first: GOG/Unity metadata, exe product name, cleaned folder name.</summary>
        public List<string> Names { get; set; } = new List<string>();
    }

    /// <summary>
    /// Crawls drives (bounded depth, time and size) for game install folders wherever they are: C:\Games, D:\Stuff\Repacks,
    /// the desktop. A folder counts when it has an engine or store SDK footprint, so ordinary apps don't show up.
    /// </summary>
    public class GameFolderFinder
    {
        public int MaxDepth { get; set; } = 5;
        public int MaxDirectories { get; set; } = 150000;
        public TimeSpan MaxTime { get; set; } = TimeSpan.FromSeconds(40);

        private static readonly HashSet<string> SkipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "$Recycle.Bin", "$WinREAgent", "System Volume Information", "Recovery", "PerfLogs", "ProgramData",
            "WindowsApps", "Common Files", "Windows Defender", "Windows Kits", "Microsoft", "Microsoft.NET", "dotnet",
            "AppData", "node_modules", ".git", ".nuget", ".vs", ".gradle", ".cache", "__pycache__", "site-packages",
            "Unity", "Unity Hub", "Epic Games\\Launcher", "Steam\\bin", "Android", "Intel", "AMD", "NVIDIA", "NVIDIA Corporation",
            "Config.Msi", "MSOCache", "OneDriveTemp", "$SysReset", "$GetCurrent", "Windows.old", "Temp", "tmp",
        };

        private static readonly Regex Noise = new Regex(
            @"\[[^\]]*\]|\([^)]*\)|\{[^}]*\}|\bv?\d+(\.\d+){1,4}\w*\b|\bbuild[ ._-]?\d+\b|\b(repack|fitgirl|dodi|elamigos|gog|goty|portable|multi\d*|x64|x86|win64|win32|steamrip|update|incl|dlcs?)\b|[-_.]+$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public List<FoundGameFolder> Find(IEnumerable<string> startDirs)
        {
            var sw = Stopwatch.StartNew();
            var found = new List<FoundGameFolder>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int visited = 0;
            var queue = new Queue<(string dir, int depth)>();
            foreach (var s in startDirs.Where(Directory.Exists)) queue.Enqueue((s, 0));

            while (queue.Count > 0)
            {
                if (sw.Elapsed > MaxTime || visited > MaxDirectories)
                {
                    Logger.Warn($"Game folder search stopped early after {visited} folders ({sw.Elapsed.TotalSeconds:0}s)");
                    break;
                }
                var (dir, depth) = queue.Dequeue();
                if (!seen.Add(dir)) continue;
                visited++;

                string[] subdirs, files;
                try
                {
                    subdirs = Directory.GetDirectories(dir);
                    files = Directory.GetFiles(dir);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    continue;
                }

                string signature = Signature(dir, subdirs, files);
                if (signature != null && depth > 0)
                {
                    found.Add(Describe(dir, subdirs, files, signature));
                    continue; // don't look inside a game for more games
                }
                if (depth >= MaxDepth) continue;
                foreach (var sub in subdirs)
                {
                    string name = System.IO.Path.GetFileName(sub);
                    if (SkipNames.Contains(name) || name.StartsWith(".") || name.StartsWith("$")) continue;
                    try
                    {
                        var attr = File.GetAttributes(sub);
                        if ((attr & (FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden)) != 0) continue;
                    }
                    catch { continue; }
                    queue.Enqueue((sub, depth + 1));
                }
            }
            Logger.Info($"Searched {visited} folders for games in {sw.ElapsedMilliseconds} ms: {found.Count} game folders");
            return found;
        }

        /// <summary>Engine or store footprint of a game install, or null.</summary>
        public static string Signature(string dir, string[] subdirs, string[] files)
        {
            var fileNames = new HashSet<string>(files.Select(System.IO.Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            var dirNames = new HashSet<string>(subdirs.Select(System.IO.Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            var exes = fileNames.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();

            if (fileNames.Contains("UnityPlayer.dll")) return "Unity";
            if (exes.Any(e => dirNames.Contains(System.IO.Path.GetFileNameWithoutExtension(e) + "_Data"))) return "Unity";
            if (dirNames.Contains("Engine") && subdirs.Any(s => Directory.Exists(System.IO.Path.Combine(s, "Content", "Paks")))) return "Unreal";
            if (fileNames.Any(f => f.StartsWith("goggame-", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".info", StringComparison.OrdinalIgnoreCase))) return "GOG";
            if (exes.Count > 0 && fileNames.Any(f => f.EndsWith(".pck", StringComparison.OrdinalIgnoreCase))) return "Godot";
            if (exes.Count > 0 && fileNames.Contains("data.win")) return "GameMaker";
            if (fileNames.Any(f => f.StartsWith("Game.rgss", StringComparison.OrdinalIgnoreCase)) || (dirNames.Contains("www") && fileNames.Contains("Game.exe"))) return "RPG Maker";

            // Store SDKs: in the folder itself or one level down (bin\, x64\, Binaries\Win64\ ...).
            string[] sdk = { "steam_api64.dll", "steam_api.dll", "steam_appid.txt", "galaxy64.dll", "Galaxy.dll", "EOSSDK-Win64-Shipping.dll", "eossdk-win64-shipping.dll" };
            if (exes.Count > 0 && sdk.Any(fileNames.Contains)) return "store SDK";
            if (exes.Count > 0 || dirNames.Count < 12)
            {
                foreach (var sub in subdirs.Take(30))
                {
                    try
                    {
                        if (sdk.Any(s => File.Exists(System.IO.Path.Combine(sub, s)))) return "store SDK";
                    }
                    catch { }
                }
            }
            return null;
        }

        private static FoundGameFolder Describe(string dir, string[] subdirs, string[] files, string signature)
        {
            var g = new FoundGameFolder { Path = dir, Signature = signature };
            void AddName(string n)
            {
                n = Clean(n);
                if (n.Length >= 2 && !g.Names.Contains(n, StringComparer.OrdinalIgnoreCase)) g.Names.Add(n);
            }

            // GOG: goggame-<id>.info is JSON with the game's name.
            foreach (var info in files.Where(f => System.IO.Path.GetFileName(f).StartsWith("goggame-", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".info", StringComparison.OrdinalIgnoreCase)))
            {
                try { AddName((string)JObject.Parse(File.ReadAllText(info))["name"]); } catch { }
            }

            // Unity: <Game>_Data\app.info has the company on line 1 and the product name on line 2.
            foreach (var data in subdirs.Where(s => s.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var lines = File.ReadAllLines(System.IO.Path.Combine(data, "app.info"));
                    if (lines.Length >= 2) AddName(lines[1]);
                }
                catch { }
            }

            // steam_appid.txt (shipped by many games) identifies the game exactly.
            foreach (var candidate in new[] { dir }.Concat(subdirs.Take(30)))
            {
                try
                {
                    string p = System.IO.Path.Combine(candidate, "steam_appid.txt");
                    if (File.Exists(p) && long.TryParse(File.ReadAllText(p).Trim().Split('\n')[0].Trim(), out long id) && id > 0)
                    {
                        g.SteamAppId = id;
                        break;
                    }
                }
                catch { }
            }

            // Exe product name: prefer the exe named like the folder, else the largest one.
            try
            {
                var exes = files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                            && !System.IO.Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                                            && System.IO.Path.GetFileName(f).IndexOf("crash", StringComparison.OrdinalIgnoreCase) < 0
                                            && System.IO.Path.GetFileName(f).IndexOf("setup", StringComparison.OrdinalIgnoreCase) < 0)
                                .OrderByDescending(f => new FileInfo(f).Length)
                                .ToList();
                string folderName = System.IO.Path.GetFileName(dir);
                var main = exes.FirstOrDefault(e => GameScanner.NormalizeName(System.IO.Path.GetFileNameWithoutExtension(e)) == GameScanner.NormalizeName(folderName))
                           ?? exes.FirstOrDefault();
                if (main != null)
                {
                    var vi = FileVersionInfo.GetVersionInfo(main);
                    if (!string.IsNullOrWhiteSpace(vi.ProductName) && !IsEngineName(vi.ProductName)) AddName(vi.ProductName);
                    if (!string.IsNullOrWhiteSpace(vi.FileDescription) && !IsEngineName(vi.FileDescription)) AddName(vi.FileDescription);
                }
            }
            catch { }

            AddName(System.IO.Path.GetFileName(dir));
            return g;
        }

        private static bool IsEngineName(string s)
            => Regex.IsMatch(s, @"^(Unity|UnityPlayer|Unreal|UE4|UE5|Godot|GameMaker|Engine|BootstrapPackagedGame|Launcher)\b", RegexOptions.IgnoreCase);

        /// <summary>"Foundation v1.9.0.5 [Repack]" -> "Foundation"; "Hollow_Knight" -> "Hollow Knight".</summary>
        public static string Clean(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string s = name.Replace('_', ' ').Replace('™', ' ').Replace('®', ' ');
            // Scene-style names use dots as spaces: "Timberborn.Update.6-GOG".
            if (s.IndexOf(' ') < 0 && s.Count(c => c == '.') >= 2) s = s.Replace('.', ' ');
            s = Regex.Replace(s, @"\b(update|patch|hotfix)[ ._-]?\d+\b", " ", RegexOptions.IgnoreCase);
            s = Noise.Replace(s, " ");
            s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '.', ',');
            return s;
        }
    }
}
