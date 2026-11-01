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

        public GameRoot() { }
        public GameRoot(string path, RootKind kind) { Path = path; Kind = kind; }

        /// <summary>The folder whose subfolders are game install folders.</summary>
        public string GamesFolder => Kind == RootKind.SteamLibrary
            ? System.IO.Path.Combine(Path, "steamapps", "common")
            : Path;

        public override string ToString() => $"{Path} ({Kind})";
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
