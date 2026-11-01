using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SaveCloud.Core.Scan
{
    /// <summary>
    /// Checks that a portable file name from the server ("&lt;winAppData&gt;/Game/slot1.sav") lies inside one of a game's
    /// save rules ("&lt;winAppData&gt;/Game/*.sav"). Downloads are only written where the game's own rules point, so a
    /// bad or malicious upload (shared servers have several people on them) can't place files elsewhere, such as the
    /// Windows Startup folder.
    /// </summary>
    public static class RuleMatcher
    {
        public static bool IsAllowed(string template, IEnumerable<string> rules, IList<string> gameNames)
            => rules.Any(r => Matches(template, r, gameNames));

        /// <summary>True when <paramref name="template"/> is matched by <paramref name="pattern"/> or lies under it.</summary>
        public static bool Matches(string template, string pattern, IList<string> gameNames)
        {
            var t = Segments(template);
            var p = Segments(pattern);
            if (t.Count == 0 || p.Count < 2) return false; // a bare token rule (whole folder tree) never authorises anything
            var regexes = p.Select(seg => SegmentRegex(seg, gameNames)).ToList();
            return Match(t, 0, regexes, 0, p);
        }

        private static bool Match(List<string> t, int ti, List<Regex> rx, int pi, List<string> raw)
        {
            if (pi == rx.Count) return ti <= t.Count && ti > 0; // pattern consumed: template equals it or lies under it
            if (raw[pi] == "**")
            {
                for (int k = ti; k <= t.Count; k++)
                    if (Match(t, k, rx, pi + 1, raw)) return true;
                return false;
            }
            if (ti >= t.Count) return false;
            return rx[pi].IsMatch(t[ti]) && Match(t, ti + 1, rx, pi + 1, raw);
        }

        private static List<string> Segments(string path)
            => path.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        private static Regex SegmentRegex(string seg, IList<string> gameNames)
        {
            var sb = new StringBuilder("^");
            int i = 0;
            while (i < seg.Length)
            {
                char c = seg[i];
                if (c == '<')
                {
                    int end = seg.IndexOf('>', i);
                    if (end > i)
                    {
                        string token = seg.Substring(i + 1, end - i - 1);
                        sb.Append(TokenRegex(token, gameNames, isWholeSegment: i == 0 && end == seg.Length - 1));
                        i = end + 1;
                        continue;
                    }
                }
                if (c == '*') sb.Append("[^/]*");
                else if (c == '?') sb.Append("[^/]");
                else sb.Append(Regex.Escape(c.ToString()));
                i++;
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string TokenRegex(string token, IList<string> gameNames, bool isWholeSegment)
        {
            switch (token.ToLowerInvariant())
            {
                case "game":
                    var names = (gameNames ?? new List<string>()).Where(n => !string.IsNullOrEmpty(n)).Select(Regex.Escape).ToList();
                    return names.Count == 0 ? "[^/]+" : "(" + string.Join("|", names) + ")";
                case "storeuserid":
                case "storegameid":
                    return "[^/]+";
                default:
                    // Folder tokens (<winAppData>, <base>, <osUserName>, ...) appear literally in templates.
                    return Regex.Escape("<" + token + ">");
            }
        }
    }
}
