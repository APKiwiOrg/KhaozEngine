using System.Collections.Generic;
using System.Globalization;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>
    /// Parses .testhealth-baseline allowances and exemptions per rule and relative path.
    /// Junk lines are skipped, the first allowance wins, and exemptions override allowances.
    /// </summary>
    public sealed class TestHealthBaseline
    {
        // Tuple string components use ordinal equality. Paths retain their '/' separators verbatim.
        private readonly Dictionary<(string RuleId, string Path), int> _entries;
        private readonly HashSet<(string RuleId, string Path)> _exempt;

        private TestHealthBaseline(Dictionary<(string RuleId, string Path), int> entries,
            HashSet<(string RuleId, string Path)> exempt)
        {
            _entries = entries;
            _exempt = exempt;
        }

        /// <summary>Returns the allowance, or zero for an absent or exempt rule and path.</summary>
        public int AllowanceFor(string ruleId, string relativePath) =>
            _entries.TryGetValue((ruleId, relativePath), out var allowance) ? allowance : 0;

        /// <summary>Whether this rule and path are exempt from checking.</summary>
        public bool IsExempt(string ruleId, string relativePath) =>
            _exempt.Contains((ruleId, relativePath));

        public static TestHealthBaseline Parse(string content)
        {
            var entries = new Dictionary<(string RuleId, string Path), int>();
            var exempt = new HashSet<(string RuleId, string Path)>();
            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                var i = 0;
                while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;

                var fieldStart = i;
                var allDigits = true;
                while (i < line.Length && line[i] != ' ' && line[i] != '\t')
                {
                    if (line[i] < '0' || line[i] > '9') allDigits = false;
                    i++;
                }
                if (i == fieldStart || i >= line.Length) continue;
                var field = line.Substring(fieldStart, i - fieldStart);
                var isExempt = field == "exempt";
                var allowance = 0;
                if (!isExempt && (!allDigits || !int.TryParse(field,
                        NumberStyles.None, CultureInfo.InvariantCulture, out allowance))) continue;

                while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
                var ruleStart = i;
                while (i < line.Length && line[i] != ' ' && line[i] != '\t') i++;
                if (i == ruleStart || i >= line.Length) continue;
                var ruleId = line.Substring(ruleStart, i - ruleStart);

                while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
                if (i >= line.Length) continue;
                var key = (ruleId, line.Substring(i));
                if (isExempt) exempt.Add(key);
                else if (!entries.ContainsKey(key)) entries.Add(key, allowance);
            }

            // Exemption wins over a numeric entry regardless of which line came first.
            foreach (var key in exempt) entries.Remove(key);
            return new TestHealthBaseline(entries, exempt);
        }
    }
}
