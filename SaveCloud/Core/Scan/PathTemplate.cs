using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SaveCloud.Core.Scan
{
    /// <summary>A save file found on disk, plus its machine-independent name.</summary>
    public class SaveFile
    {
        public string ActualPath { get; set; }
        /// <summary>Portable path such as "&lt;winAppData&gt;/Game/slot1.sav", identical on every PC.</summary>
        public string Template { get; set; }
        public long Size { get; set; }
        public DateTime ModifiedUtc { get; set; }
        /// <summary>Length of the manifest pattern that matched; longer = more specific (used to settle shared files).</summary>
        internal int PatternSpecificity { get; set; }
    }

    /// <summary>
    /// Expands manifest patterns ("&lt;winAppData&gt;/Game/**/*.sav") into files on disk, and maps portable
    /// templates back to real paths on this PC for restoring.
    /// </summary>
    public class PathTemplate
    {
        public const int MaxFilesPerPattern = 5000;

        private readonly PathEnvironment _env;

        public PathTemplate(PathEnvironment env) { _env = env; }

        /// <summary>
        /// Finds every file matching <paramref name="pattern"/>. <paramref name="basePaths"/> are the game's install
        /// folders on this PC (used for &lt;base&gt;); <paramref name="gameNames"/> are the values tried for &lt;game&gt;.
        /// </summary>
        public List<SaveFile> Expand(string pattern, IList<string> basePaths, IList<string> gameNames, long? steamId)
        {
            var results = new List<SaveFile>();
            try
            {
                var segs = Split(pattern);
                if (segs.Count < 2) return results; // a bare "<base>" or "<home>" would mean an entire folder tree

                foreach (var (prefixPath, prefixTemplate, gameName) in ResolveLeading(segs[0], basePaths, gameNames))
                {
                    var rest = new List<(string seg, bool keepToken)>();
                    bool ok = true;
                    foreach (var raw in segs.Skip(1))
                    {
                        var s = SubstituteInner(raw, gameName, steamId, out bool keep);
                        if (s == null || s.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || s.IndexOfAny(new[] { '<', '>', '|', '"' }) >= 0)
                        {
                            ok = false;
                            break;
                        }
                        rest.Add((s, keep));
                    }
                    if (!ok) continue;

                    Walk(prefixPath, new List<string> { prefixTemplate }, rest, 0, results, pattern.Length);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException
                                       || ex is NotSupportedException || ex is System.Security.SecurityException)
            {
                // Unreadable folders are skipped, not fatal.
            }

            // A pattern can be reached through two routes (e.g. two install folders); keep one entry per file.
            return results
                .GroupBy(f => f.ActualPath, _env.Comparer)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>
        /// Turns a template back into a path on this PC. Returns null when it can't be placed here
        /// (e.g. &lt;base&gt; for a game that isn't installed on this PC).
        /// </summary>
        public string Resolve(string template, IList<string> basePaths)
        {
            var segs = Split(template);
            if (segs.Count == 0) return null;

            string head;
            string first = segs[0];
            if (IsToken(first, out string token))
            {
                if (token.Equals("base", StringComparison.OrdinalIgnoreCase))
                    head = basePaths?.FirstOrDefault();
                else if (token.Equals("root", StringComparison.OrdinalIgnoreCase))
                    head = _env.SteamRoot;
                else
                    _env.Folders.TryGetValue(token, out head);
            }
            else
            {
                head = first; // absolute path such as "C:"
            }
            if (string.IsNullOrEmpty(head)) return null;

            var parts = new List<string> { head };
            foreach (var s in segs.Skip(1))
            {
                if (s.Equals("<osUserName>", StringComparison.OrdinalIgnoreCase)) parts.Add(_env.OsUserName);
                else if (s.IndexOf('<') >= 0) return null;
                // Never let a template (which comes from the server) escape its folder.
                else if (s == "." || s == ".." || s.IndexOf(':') >= 0 || Path.IsPathRooted(s)
                         || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
                else parts.Add(s);
            }
            return Combine(parts);
        }

        // ---- internals ---------------------------------------------------------------------------------

        private static List<string> Split(string pattern)
            => pattern.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        private static bool IsToken(string seg, out string token)
        {
            token = null;
            if (seg.Length > 2 && seg[0] == '<' && seg[seg.Length - 1] == '>' && seg.IndexOf('<', 1) < 0)
            {
                token = seg.Substring(1, seg.Length - 2);
                return true;
            }
            return false;
        }

        private IEnumerable<(string path, string template, string gameName)> ResolveLeading(
            string first, IList<string> basePaths, IList<string> gameNames)
        {
            var names = gameNames ?? new List<string>();
            if (IsToken(first, out string token))
            {
                if (token.Equals("base", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var b in basePaths ?? new List<string>())
                        yield return (b, "<base>", Path.GetFileName(b.TrimEnd('\\', '/')));
                }
                else if (token.Equals("root", StringComparison.OrdinalIgnoreCase))
                {
                    // In the manifest, <root> without <base> is almost always Steam's userdata folder.
                    if (!string.IsNullOrEmpty(_env.SteamRoot))
                        foreach (var n in names.DefaultIfEmpty(null))
                            yield return (_env.SteamRoot, "<root>", n);
                }
                else if (_env.Folders.TryGetValue(token, out string folder) && !string.IsNullOrEmpty(folder))
                {
                    foreach (var n in names.DefaultIfEmpty(null))
                        yield return (folder, "<" + PathEnvironment.FolderTokens.First(t => t.Equals(token, StringComparison.OrdinalIgnoreCase)) + ">", n);
                }
                // Unknown placeholder: pattern doesn't apply on Windows.
            }
            else if (first.Length == 2 && first[1] == ':' && char.IsLetter(first[0]))
            {
                foreach (var n in names.DefaultIfEmpty(null))
                    yield return (first + Path.DirectorySeparatorChar, first.ToUpperInvariant(), n);
            }
        }

        /// <summary>Replaces placeholders inside a path segment. Returns null when one can't be resolved.</summary>
        private string SubstituteInner(string seg, string gameName, long? steamId, out bool keepAsToken)
        {
            keepAsToken = false;
            if (seg.IndexOf('<') < 0) return seg;

            if (seg.Equals("<osUserName>", StringComparison.OrdinalIgnoreCase))
            {
                keepAsToken = true; // stored as <osUserName> so it restores under a different Windows username
                return _env.OsUserName;
            }

            string s = seg;
            s = ReplaceToken(s, "game", gameName);
            s = ReplaceToken(s, "osUserName", _env.OsUserName);
            s = ReplaceToken(s, "storeUserId", "*");
            s = ReplaceToken(s, "storeGameId", steamId?.ToString() ?? "*");
            return s.IndexOf('<') >= 0 ? null : s;
        }

        private static string ReplaceToken(string s, string token, string value)
        {
            string t = "<" + token + ">";
            int i = s.IndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return s;
            if (value == null) return "<unresolved>";
            return s.Substring(0, i) + value + ReplaceToken(s.Substring(i + t.Length), token, value);
        }

        private void Walk(string dir, List<string> template, List<(string seg, bool keep)> rest, int index,
                          List<SaveFile> results, int specificity)
        {
            if (results.Count >= MaxFilesPerPattern) return;

            if (index == rest.Count)
            {
                if (File.Exists(dir)) Add(dir, template, results, specificity);
                else if (Directory.Exists(dir)) AddTree(dir, template, results, specificity);
                return;
            }

            if (!Directory.Exists(dir)) return;

            var (seg, keep) = rest[index];
            bool last = index == rest.Count - 1;

            if (seg == "**")
            {
                // Zero or more folders.
                Walk(dir, template, rest, index + 1, results, specificity);
                foreach (var sub in SafeDirs(dir))
                    Walk(sub, Append(template, Path.GetFileName(sub)), rest, index, results, specificity);
                return;
            }

            if (seg.IndexOf('*') >= 0 || seg.IndexOf('?') >= 0)
            {
                foreach (var sub in SafeDirs(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (Wildcard.IsMatch(name, seg, _env.IgnoreCase))
                        Walk(sub, Append(template, name), rest, index + 1, results, specificity);
                }
                if (last)
                {
                    foreach (var file in SafeFiles(dir))
                    {
                        string name = Path.GetFileName(file);
                        if (Wildcard.IsMatch(name, seg, _env.IgnoreCase))
                            Add(file, Append(template, name), results, specificity);
                    }
                }
                return;
            }

            string next = Path.Combine(dir, seg);
            // Use the real on-disk casing for the template so two PCs agree on names.
            string actualName = keep ? "<osUserName>" : RealName(dir, seg);
            Walk(next, Append(template, actualName), rest, index + 1, results, specificity);
        }

        private string RealName(string dir, string seg)
        {
            if (!_env.IgnoreCase) return seg;
            try
            {
                foreach (var e in Directory.EnumerateFileSystemEntries(dir, seg))
                    return Path.GetFileName(e);
            }
            catch { }
            return seg;
        }

        private void AddTree(string dir, List<string> template, List<SaveFile> results, int specificity)
        {
            foreach (var file in SafeFiles(dir))
            {
                if (results.Count >= MaxFilesPerPattern) return;
                Add(file, Append(template, Path.GetFileName(file)), results, specificity);
            }
            foreach (var sub in SafeDirs(dir))
            {
                if (results.Count >= MaxFilesPerPattern) return;
                AddTree(sub, Append(template, Path.GetFileName(sub)), results, specificity);
            }
        }

        private static void Add(string file, List<string> template, List<SaveFile> results, int specificity)
        {
            try
            {
                var fi = new FileInfo(file);
                results.Add(new SaveFile
                {
                    ActualPath = fi.FullName,
                    Template = string.Join("/", template),
                    Size = fi.Length,
                    ModifiedUtc = fi.LastWriteTimeUtc,
                    PatternSpecificity = specificity,
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static List<string> Append(List<string> list, string item) => new List<string>(list) { item };

        private static IEnumerable<string> SafeDirs(string dir)
        {
            try
            {
                return Directory.GetDirectories(dir).Where(d => !IsReparsePoint(d));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return Enumerable.Empty<string>();
        }

        private static IEnumerable<string> SafeFiles(string dir)
        {
            try { return Directory.GetFiles(dir); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return Enumerable.Empty<string>();
        }

        // Junctions/symlinks can loop (e.g. "Application Data" inside AppData); don't follow them.
        private static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }

        private static string Combine(List<string> parts)
        {
            string p = parts[0];
            if (p.Length == 2 && p[1] == ':') p += Path.DirectorySeparatorChar;
            for (int i = 1; i < parts.Count; i++) p = Path.Combine(p, parts[i]);
            return p;
        }
    }

    internal static class Wildcard
    {
        /// <summary>Glob match supporting * and ? within a single path segment.</summary>
        public static bool IsMatch(string text, string pattern, bool ignoreCase)
        {
            int t = 0, p = 0, star = -1, mark = 0;
            while (t < text.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || Eq(pattern[p], text[t], ignoreCase)))
                {
                    t++; p++;
                }
                else if (p < pattern.Length && pattern[p] == '*')
                {
                    star = p++;
                    mark = t;
                }
                else if (star >= 0)
                {
                    p = star + 1;
                    t = ++mark;
                }
                else return false;
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }

        private static bool Eq(char a, char b, bool ignoreCase)
            => ignoreCase ? char.ToUpperInvariant(a) == char.ToUpperInvariant(b) : a == b;
    }
}
