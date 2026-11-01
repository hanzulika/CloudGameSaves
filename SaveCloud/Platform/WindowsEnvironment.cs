using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using SaveCloud.Core;
using SaveCloud.Core.Scan;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SaveCloud.Platform
{
    /// <summary>Works out what the manifest placeholders and game libraries are on this Windows PC.</summary>
    public static class WindowsEnvironment
    {
        public static PathEnvironment Build(AppConfig config)
        {
            var env = new PathEnvironment { IgnoreCase = true, OsUserName = Environment.UserName };

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            env.Folders["home"] = home;
            env.Folders["winAppData"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            env.Folders["winLocalAppData"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            env.Folders["winLocalAppDataLow"] = KnownFolder(new Guid("A520A1A4-1780-4FF6-BD18-167343C5AF16"))
                                                ?? Path.Combine(home, "AppData", "LocalLow");
            // MyDocuments follows OneDrive/folder redirection, which is exactly why saves are stored as <winDocuments>/...
            env.Folders["winDocuments"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            env.Folders["winPublic"] = Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public";
            env.Folders["winProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            env.Folders["winDir"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            AddSteam(env);
            AddEpic(env);
            AddGog(env);

            foreach (var pf in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            })
                AddRoot(env, pf, RootKind.Plain);

            foreach (var drive in SafeDrives())
                foreach (var name in new[] { "Games", "Program Files\\EA Games", "Program Files (x86)\\Ubisoft\\Ubisoft Game Launcher\\games" })
                    AddRoot(env, Path.Combine(drive, name), RootKind.Plain);

            foreach (var extra in config.ExtraRoots ?? new List<string>())
            {
                // A user-added folder may itself be a Steam library.
                bool steamLib = Directory.Exists(Path.Combine(extra, "steamapps", "common"));
                AddRoot(env, extra, steamLib ? RootKind.SteamLibrary : RootKind.Plain);
                if (steamLib) AddSteamAppIds(env, extra);
            }

            Logger.Info("Game folders: " + string.Join("; ", env.Roots.Select(r => r.ToString())));
            return env;
        }

        private static void AddRoot(PathEnvironment env, string path, RootKind kind)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = Path.GetFullPath(path).TrimEnd('\\');
                if (!Directory.Exists(path)) return;
                if (env.Roots.Any(r => r.Kind == kind && string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))) return;
                env.Roots.Add(new GameRoot(path, kind));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is NotSupportedException) { }
        }

        private static void AddSteam(PathEnvironment env)
        {
            string steam = RegString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath")
                           ?? RegString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
                           ?? RegString(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            if (steam == null) return;
            steam = steam.Replace('/', '\\');
            if (!Directory.Exists(steam)) return;
            env.SteamRoot = steam;

            var libraries = new List<string> { steam };
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            try
            {
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\""))
                        libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
            catch (IOException) { }

            foreach (var lib in libraries)
            {
                AddRoot(env, lib, RootKind.SteamLibrary);
                AddSteamAppIds(env, lib);
            }
        }

        private static void AddSteamAppIds(PathEnvironment env, string library)
        {
            try
            {
                string apps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(apps)) return;
                foreach (var f in Directory.GetFiles(apps, "appmanifest_*.acf"))
                {
                    var m = Regex.Match(Path.GetFileName(f), @"appmanifest_(\d+)\.acf");
                    if (m.Success && long.TryParse(m.Groups[1].Value, out long id)) env.SteamInstalledAppIds.Add(id);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        private static void AddEpic(PathEnvironment env)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                      "Epic", "EpicGamesLauncher", "Data", "Manifests");
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var item in Directory.GetFiles(dir, "*.item"))
                {
                    try
                    {
                        string loc = (string)JObject.Parse(File.ReadAllText(item))["InstallLocation"];
                        if (!string.IsNullOrEmpty(loc)) AddRoot(env, Path.GetDirectoryName(loc.TrimEnd('\\', '/')), RootKind.Plain);
                    }
                    catch (Exception) { /* one bad manifest shouldn't hide the others */ }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        private static void AddGog(PathEnvironment env)
        {
            try
            {
                using (var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games"))
                {
                    if (games == null) return;
                    foreach (var id in games.GetSubKeyNames())
                    {
                        string path = RegString(games, id, "path");
                        if (!string.IsNullOrEmpty(path)) AddRoot(env, Path.GetDirectoryName(path.TrimEnd('\\')), RootKind.Plain);
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException) { }
        }

        private static IEnumerable<string> SafeDrives()
        {
            try
            {
                return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
            }
            catch (IOException) { return Enumerable.Empty<string>(); }
        }

        private static string RegString(RegistryKey hive, string subKey, string name)
        {
            try
            {
                using (var k = hive.OpenSubKey(subKey))
                    return k?.GetValue(name) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
                return null;
            }
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        private static string KnownFolder(Guid id)
        {
            try
            {
                if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out IntPtr p) != 0) return null;
                try { return Marshal.PtrToStringUni(p); }
                finally { Marshal.FreeCoTaskMem(p); }
            }
            catch { return null; }
        }
    }
}
