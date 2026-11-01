using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace SaveCloud.Core
{
    /// <summary>Per-PC settings, stored in %AppData%\SaveCloud\config.json. Everything has a working default.</summary>
    public class AppConfig
    {
        // --- Server ---
        /// <summary>Manual server address (e.g. http://100.101.102.103:8765). Empty = find it automatically via Tailscale.</summary>
        public string ServerUrl { get; set; } = "";
        /// <summary>The server found last time; tried first on the next start.</summary>
        public string LastServerUrl { get; set; } = "";

        // --- Game detection ---
        /// <summary>Extra folders that contain installed games (e.g. D:\Games). Steam/Epic/GOG are found automatically.</summary>
        public List<string> ExtraRoots { get; set; } = new List<string>();
        /// <summary>Games the user never wants synced (exact manifest names).</summary>
        public List<string> IgnoredGames { get; set; } = new List<string>();
        /// <summary>Also sync files the manifest tags only as "config" (graphics/keybind settings). Off by default:
        /// those are usually PC-specific (resolution, GPU settings) and syncing them causes more harm than good.</summary>
        public bool IncludeConfigFiles { get; set; } = false;
        /// <summary>Skip games that are installed through Steam AND use Steam Cloud, so two sync systems don't fight.</summary>
        public bool SkipSteamCloudGames { get; set; } = true;
        /// <summary>Games whose saves exceed this are skipped (protects against manifest entries that match a whole install folder).</summary>
        public int MaxGameSizeMB { get; set; } = 512;

        // --- Sync behaviour ---
        public bool StartWithWindows { get; set; } = true;
        /// <summary>How many old revisions per game to keep on the server.</summary>
        public int HistoryToKeep { get; set; } = 10;
        /// <summary>How often to check the server for saves uploaded by another PC.</summary>
        public int PullIntervalMinutes { get; set; } = 10;
        /// <summary>How often to check local save files for changes (catches games whose process can't be tracked).</summary>
        public int ScanIntervalMinutes { get; set; } = 5;

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(AppPaths.ConfigFile))
                    return JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(AppPaths.ConfigFile)) ?? new AppConfig();
            }
            catch (Exception ex)
            {
                Logger.Error("Could not read config, using defaults", ex);
            }
            return new AppConfig();
        }

        public void Save() => JsonFile.WriteAtomic(AppPaths.ConfigFile, this);
    }

    internal static class JsonFile
    {
        /// <summary>Writes JSON to a temp file then swaps it in, so a crash never leaves a half-written file.</summary>
        public static void WriteAtomic(string path, object value)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(value, Formatting.Indented));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }
}
