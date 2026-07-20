using System.Collections.Generic;
using System.Globalization;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>
    /// Parses .filesize-baseline with the same tolerance as check-file-size.sh's awk reader: a line
    /// whose first whitespace-delimited field is all digits is an entry ("lines path", the path may
    /// contain spaces), a line whose first field is exactly "exempt" marks a path as not checked at
    /// all, everything else (comments, blanks, junk) is skipped silently, and the first entry for a
    /// path wins, matching the awk exit-on-first-match.
    /// </summary>
    public static class BaselineFile
    {
        /// <summary>
        /// A parsed baseline: the frozen line counts, plus the paths exempted from every check.
        /// An exempt path never appears in <see cref="Entries"/>, so exemption wins over a numeric
        /// entry for the same path no matter which line came first.
        /// </summary>
        public sealed class Baseline
        {
            internal Baseline(Dictionary<string, int> entries, HashSet<string> exempt)
            {
                Entries = entries;
                Exempt = exempt;
            }

            /// <summary>Frozen line count per relative path. Exempt paths are excluded.</summary>
            public Dictionary<string, int> Entries { get; }

            /// <summary>Relative paths that are not checked at all: no baseline, no cap.</summary>
            public HashSet<string> Exempt { get; }

            public bool IsExempt(string relativePath) => Exempt.Contains(relativePath);
        }

        public static Baseline Parse(string content)
        {
            var entries = new Dictionary<string, int>();
            var exempt = new HashSet<string>();
            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                var i = 0;
                while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;

                if (TryReadExemptPath(line, i, out var exemptPath))
                {
                    exempt.Add(exemptPath);
                    continue;
                }

                var digitsStart = i;
                while (i < line.Length && line[i] >= '0' && line[i] <= '9') i++;
                if (i == digitsStart) continue;
                if (i >= line.Length || (line[i] != ' ' && line[i] != '\t')) continue;
                if (!int.TryParse(line.Substring(digitsStart, i - digitsStart),
                        NumberStyles.None, CultureInfo.InvariantCulture, out var lines)) continue;
                while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
                if (i >= line.Length) continue;
                var path = line.Substring(i);
                if (!entries.ContainsKey(path)) entries.Add(path, lines);
            }

            // Exemption is the more explicit statement, so it wins over a numeric entry regardless of
            // which line came first.
            foreach (var path in exempt) entries.Remove(path);
            return new Baseline(entries, exempt);
        }

        // The keyword is exactly "exempt", lowercase, followed by at least one space or tab, then the
        // rest of the line verbatim as the path (paths may contain spaces, same as numeric entries).
        private static bool TryReadExemptPath(string line, int start, out string path)
        {
            const string Keyword = "exempt";
            path = string.Empty;
            if (start + Keyword.Length >= line.Length) return false;
            if (string.CompareOrdinal(line, start, Keyword, 0, Keyword.Length) != 0) return false;

            var i = start + Keyword.Length;
            if (line[i] != ' ' && line[i] != '\t') return false;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            if (i >= line.Length) return false;
            path = line.Substring(i);
            return true;
        }
    }
}
