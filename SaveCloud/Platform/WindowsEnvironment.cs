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
using System.Threading;

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
            AddEa(env);
            AddUbisoft(env);
            AddInstalledPrograms(env);
            AddShortcuts(env);

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
            Logger.Info($"Installed programs and shortcuts considered: {env.Installs.Count}");
            return env;
        }

        private static void AddRoot(PathEnvironment env, string path, RootKind kind, string store = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = Path.GetFullPath(path).TrimEnd('\\');
                if (!Directory.Exists(path)) return;
                if (env.Roots.Any(r => r.Kind == kind && string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))) return;
                env.Roots.Add(new GameRoot(path, kind, store));
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
                AddRoot(env, lib, RootKind.SteamLibrary, "steam");
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
                        var json = JObject.Parse(File.ReadAllText(item));
                        string loc = (string)json["InstallLocation"];
                        if (string.IsNullOrEmpty(loc)) continue;
                        AddRoot(env, Path.GetDirectoryName(loc.TrimEnd('\\', '/')), RootKind.Plain);
                        AddInstall(env, loc, (string)json["DisplayName"], "epic", "Epic Games");
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
                        if (string.IsNullOrEmpty(path)) continue;
                        AddRoot(env, Path.GetDirectoryName(path.TrimEnd('\\')), RootKind.Plain);
                        AddInstall(env, path, RegString(games, id, "gameName"), "gog", "GOG Galaxy");
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException) { }
        }

        private static void AddEa(PathEnvironment env)
        {
            foreach (var parent in new[] { @"SOFTWARE\WOW6432Node\Electronic Arts", @"SOFTWARE\WOW6432Node\EA Games", @"SOFTWARE\Electronic Arts" })
                ForEachSubKey(Registry.LocalMachine, parent, (key, name) =>
                    AddInstall(env, key.GetValue("Install Dir") as string, name, "origin", "EA app"));
        }

        private static void AddUbisoft(PathEnvironment env)
        {
            ForEachSubKey(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs", (key, _) =>
                AddInstall(env, key.GetValue("InstallDir") as string, null, "uplay", "Ubisoft Connect"));
        }

        /// <summary>
        /// Windows' installed-programs list (Settings > Apps). Covers games from any installer, including ones
        /// installed outside the usual game folders.
        /// </summary>
        private static void AddInstalledPrograms(PathEnvironment env)
        {
            var hives = new[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            };
            foreach (var (hive, path) in hives)
                ForEachSubKey(hive, path, (key, keyName) =>
                {
                    if (key.GetValue("SystemComponent") is int sc && sc == 1) return;
                    string name = key.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name)) return;
                    string dir = key.GetValue("InstallLocation") as string;
                    if (string.IsNullOrWhiteSpace(dir)) dir = DirOfExe(key.GetValue("DisplayIcon") as string);
                    if (string.IsNullOrWhiteSpace(dir))
                    {
                        // Inno Setup (used by many game installers) puts unins000.exe in the install folder.
                        string un = DirOfExe(key.GetValue("UninstallString") as string);
                        if (un != null && File.Exists(Path.Combine(un, "unins000.exe"))) dir = un;
                    }
                    string store = keyName.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase) ? "steam" : null;
                    AddInstall(env, dir, name, store, "installed programs");
                });
        }

        /// <summary>Desktop and Start-menu shortcuts: their target's folder is the game's install folder.</summary>
        private static void AddShortcuts(PathEnvironment env)
        {
            var folders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            };
            var links = new List<string>();
            foreach (var f in folders.Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f)))
            {
                try { links.AddRange(Directory.EnumerateFiles(f, "*.lnk", SearchOption.AllDirectories).Take(2000)); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            if (links.Count == 0) return;

            // WScript.Shell is a COM object; resolve shortcuts on an STA thread.
            var resolved = new List<(string name, string target)>();
            var t = new Thread(() =>
            {
                try
                {
                    var type = Type.GetTypeFromProgID("WScript.Shell");
                    object shell = Activator.CreateInstance(type);
                    try
                    {
                        foreach (var lnk in links)
                        {
                            try
                            {
                                object sc = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                                string target = (string)sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null);
                                Marshal.FinalReleaseComObject(sc);
                                if (!string.IsNullOrEmpty(target)) resolved.Add((Path.GetFileNameWithoutExtension(lnk), target));
                            }
                            catch (Exception) { /* broken shortcut */ }
                        }
                    }
                    finally { Marshal.FinalReleaseComObject(shell); }
                }
                catch (Exception ex) { Logger.Warn($"Could not read shortcuts: {ex.Message}"); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join(TimeSpan.FromSeconds(30));

            foreach (var (name, target) in resolved)
            {
                if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                AddInstall(env, GameFolderOf(target), name, null, "shortcut");
            }
        }

        /// <summary>Folder of a game's exe, stepping out of bin\x64\Win64-style subfolders to the game's own folder.</summary>
        private static string GameFolderOf(string exe)
        {
            string dir = Path.GetDirectoryName(exe);
            for (int i = 0; i < 3 && dir != null; i++)
            {
                string n = Path.GetFileName(dir).ToLowerInvariant();
                if (n == "bin" || n == "binaries" || n == "x64" || n == "x86" || n == "win64" || n == "win32" ||
                    n == "bin64" || n == "bin32" || n == "game" || n == "retail" || n.StartsWith("bin_"))
                    dir = Path.GetDirectoryName(dir);
                else break;
            }
            return dir;
        }

        private static string DirOfExe(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = value.Trim();
            if (v.StartsWith("\"")) v = v.Substring(1, Math.Max(0, v.IndexOf('"', 1) - 1));
            int comma = v.LastIndexOf(',');
            if (comma > 2 && !v.Substring(comma).Contains("\\")) v = v.Substring(0, comma);
            int exe = v.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0) v = v.Substring(0, exe + 4);
            try { return File.Exists(v) ? Path.GetDirectoryName(v) : null; }
            catch (ArgumentException) { return null; }
        }

        private static void AddInstall(PathEnvironment env, string dir, string name, string store, string source)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                dir = Path.GetFullPath(dir.Trim().Trim('"')).TrimEnd('\\');
                if (dir.Length <= 3 || !Directory.Exists(dir)) return; // never a whole drive
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (dir.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return;
                var existing = env.Installs.FirstOrDefault(i => string.Equals(i.Path, dir, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.Store = existing.Store ?? store;
                    if (string.IsNullOrEmpty(existing.DisplayName)) existing.DisplayName = name;
                    return;
                }
                env.Installs.Add(new InstallCandidate { Path = dir, DisplayName = name, Store = store, Source = source });
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is NotSupportedException || ex is System.Security.SecurityException) { }
        }

        private static void ForEachSubKey(RegistryKey hive, string path, Action<RegistryKey, string> action)
        {
            try
            {
                using (var parent = hive.OpenSubKey(path))
                {
                    if (parent == null) return;
                    foreach (var name in parent.GetSubKeyNames())
                    {
                        try
                        {
                            using (var key = parent.OpenSubKey(name))
                                if (key != null) action(key, name);
                        }
                        catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException) { }
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
