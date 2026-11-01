using System.Collections.Generic;

namespace SaveCloud.Core.Manifest
{
    /// <summary>One game from the Ludusavi manifest, reduced to what a Windows PC needs.</summary>
    public class ManifestGame
    {
        public string Name { get; set; }
        /// <summary>Folder names the game is installed under (used to resolve &lt;base&gt; and &lt;game&gt;).</summary>
        public List<string> InstallDirs { get; set; } = new List<string>();
        public List<FileRule> Files { get; set; } = new List<FileRule>();
        public long? SteamId { get; set; }
        /// <summary>The game uses Steam Cloud when bought on Steam.</summary>
        public bool SteamCloud { get; set; }
        /// <summary>Stores whose own cloud saves this game uses: steam, epic, gog, origin (EA), uplay (Ubisoft).</summary>
        public List<string> CloudStores { get; set; } = new List<string>();
        /// <summary>A game the user added by hand (not from the manifest).</summary>
        public bool Custom { get; set; }
    }

    public class FileRule
    {
        /// <summary>Path pattern with placeholders and globs, e.g. "&lt;winAppData&gt;/Game/*.sav".</summary>
        public string Pattern { get; set; }
        public bool IsSave { get; set; }
        public bool IsConfig { get; set; }
    }
}
