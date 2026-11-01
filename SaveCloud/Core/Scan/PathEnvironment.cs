using System;
using System.Collections.Generic;

namespace SaveCloud.Core.Scan
{
    public enum RootKind
    {
        /// <summary>A folder whose subfolders are games: &lt;base&gt; = root\&lt;installDir&gt;.</summary>
        Plain,
        /// <summary>A Steam library: &lt;base&gt; = root\steamapps\common\&lt;installDir&gt;.</summary>
        SteamLibrary,
    }

    public class GameRoot
    {
        public string Path { get; set; }
        public RootKind Kind { get; set; }
        /// <summary>Launcher that owns this library (steam, epic, gog, origin, uplay), or null.</summary>
        public string Store { get; set; }

        public GameRoot() { }
        public GameRoot(string path, RootKind kind, string store = null) { Path = path; Kind = kind; Store = store; }

        /// <summary>The folder whose subfolders are game install folders.</summary>
        public string GamesFolder => Kind == RootKind.SteamLibrary
            ? System.IO.Path.Combine(Path, "steamapps", "common")
            : Path;

        public override string ToString() => $"{Path} ({Kind})";
    }

    /// <summary>
    /// A program's install folder found outside the game libraries: Windows' installed-programs list, desktop and
    /// Start-menu shortcuts, or a launcher's own records.
    /// </summary>
    public class InstallCandidate
    {
        public string Path { get; set; }
        /// <summary>Name shown by Windows or on the shortcut, e.g. "The Sims 4 DX9".</summary>
        public string DisplayName { get; set; }
        /// <summary>Launcher it was installed with (steam, epic, gog, origin, uplay), or null if unknown.</summary>
        public string Store { get; set; }
        public string Source { get; set; }
        /// <summary>Steam app id read from the folder (steam_appid.txt), when present: identifies the game exactly.</summary>
        public long? SteamAppId { get; set; }
        /// <summary>More candidate names from the folder's metadata (GOG info, Unity app.info, exe product name).</summary>
        public List<string> Hints { get; set; } = new List<string>();
        /// <summary>Found by the drive-wide game folder search (has an engine/SDK footprint, so it's surely a game).</summary>
        public bool IsGameFolder { get; set; }
        public override string ToString() => $"{DisplayName} -> {Path} ({Source}{(Store == null ? "" : ", " + Store)})";
    }

    /// <summary>
    /// What the manifest placeholders mean on this PC. Built by the platform layer (registry, known folders)
    /// and kept separate so the scanner can be tested on any OS.
    /// </summary>
    public class PathEnvironment
    {
        /// <summary>Placeholder name (without angle brackets, e.g. "winAppData") to absolute folder.</summary>
        public Dictionary<string, string> Folders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string OsUserName { get; set; } = Environment.UserName;
        public List<GameRoot> Roots { get; } = new List<GameRoot>();
        public List<InstallCandidate> Installs { get; } = new List<InstallCandidate>();
        /// <summary>Steam's own install folder (has userdata\), or null when Steam isn't installed.</summary>
        public string SteamRoot { get; set; }
        /// <summary>App IDs that have an appmanifest_*.acf in some Steam library, i.e. installed through Steam.</summary>
        public HashSet<long> SteamInstalledAppIds { get; } = new HashSet<long>();
        /// <summary>True on Windows: file names compare case-insensitively.</summary>
        public bool IgnoreCase { get; set; } = true;

        public StringComparison Comparison => IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        public StringComparer Comparer => IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>Placeholders that stand for a fixed per-user folder and are portable between PCs.</summary>
        public static readonly string[] FolderTokens =
        {
            "home", "winAppData", "winLocalAppData", "winLocalAppDataLow", "winDocuments",
            "winPublic", "winProgramData", "winDir",
        };
    }
}
