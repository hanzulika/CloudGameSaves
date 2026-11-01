using System;
using System.IO;

namespace SaveCloud.Core
{
    /// <summary>Where SaveCloud keeps its own files on this PC.</summary>
    public static class AppPaths
    {
        private static string _dataDir;
        private static string _cacheDir;

        /// <summary>%AppData%\SaveCloud: config and sync state (small, roams with the profile).</summary>
        public static string DataDir
        {
            get => _dataDir ?? (_dataDir = Ensure(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SaveCloud")));
            set => _dataDir = Ensure(value);
        }

        /// <summary>%LocalAppData%\SaveCloud: manifest cache, logs, local safety backups.</summary>
        public static string CacheDir
        {
            get => _cacheDir ?? (_cacheDir = Ensure(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SaveCloud")));
            set => _cacheDir = Ensure(value);
        }

        public static string ConfigFile => Path.Combine(DataDir, "config.json");
        public static string StateFile => Path.Combine(DataDir, "state.json");
        public static string DefaultKeyFile => Path.Combine(DataDir, "id_ed25519");
        public static string ManifestYaml => Path.Combine(CacheDir, "ludusavi-manifest.yaml");
        public static string ManifestCache => Path.Combine(CacheDir, "manifest-windows.json");
        public static string LogDir => Ensure(Path.Combine(CacheDir, "Logs"));
        public static string BackupDir => Ensure(Path.Combine(CacheDir, "Backups"));
        public static string TempDir => Ensure(Path.Combine(CacheDir, "Temp"));

        private static string Ensure(string dir)
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
