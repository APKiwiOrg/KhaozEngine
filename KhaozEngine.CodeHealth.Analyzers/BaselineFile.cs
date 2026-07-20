using System.Collections.Generic;
using System.Globalization;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>
    /// Parses .filesize-baseline with the same tolerance as check-file-size.sh's awk reader: a line
    /// whose first whitespace-delimited field is all digits is an entry ("lines path", the path may
    /// contain spaces), everything else (comments, blanks, junk) is skipped silently, and the first
    /// entry for a path wins, matching the awk exit-on-first-match.
    /// </summary>
    public static class BaselineFile
    {
        public static Dictionary<string, int> Parse(string content)
        {
            var entries = new Dictionary<string, int>();
            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                var i = 0;
                while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
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
            return entries;
        }
    }
}
