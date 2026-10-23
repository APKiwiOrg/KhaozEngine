using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace KhaozEngine.Diagnostics;

/// <summary>An immutable, literal-only snapshot of a game's reporter identity terms.</summary>
internal sealed class ReporterIdentityRedaction(Regex? pattern, bool maskAll = false)
{
    const int MinimumTermLength = 3;

    // Bound construction independently of the input-length bound used during matching.
    const int MaximumPatternLength = 4096;

    static readonly ReporterIdentityRedaction Failed = new(null, maskAll: true);
    static readonly ReporterIdentityRedaction Empty = new(null);

    internal Regex? Pattern => pattern;

    /// <summary>Reads the source once. Any source or pattern construction failure masks every string.</summary>
    internal static ReporterIdentityRedaction Capture(Func<IEnumerable<string>> source, TimeSpan timeout)
    {
        try
        {
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var terms = new List<string>();
            int length = 0;
            foreach (string? term in source())
            {
                if (term is null || term.Length < MinimumTermLength || string.IsNullOrWhiteSpace(term)) continue;
                if (term.Length > MaximumPatternLength) return Failed;
                if (!unique.Add(term)) continue;
                length += Regex.Escape(term).Length + (terms.Count == 0 ? 0 : 1);
                if (length > MaximumPatternLength) return Failed;
                terms.Add(term);
            }
            if (terms.Count == 0) return Empty;

            // One replacement pass with longest alternatives first keeps a short name from exposing its suffix.
            terms.Sort(static (left, right) => right.Length.CompareTo(left.Length));
            var expression = new StringBuilder(length);
            foreach (string term in terms)
            {
                if (expression.Length > 0) expression.Append('|');
                expression.Append(Regex.Escape(term));
            }
            return new ReporterIdentityRedaction(new Regex(expression.ToString(),
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, timeout));
        }
        catch (Exception)
        {
            // Even an enumerator or a pattern the non-backtracking engine refuses must not expose identity.
            return Failed;
        }
    }

    /// <summary>Preserves clean instances without allocating and fails closed on excessive size or matching failure.</summary>
    internal string Redact(string text)
    {
        if (maskAll || text.Length > ReportRedactor.MaximumInputLength) return ReportRedactor.Mask;
        try
        {
            string result = pattern is not null && pattern.IsMatch(text) ? pattern.Replace(text, ReportRedactor.Mask) : text;
            return result.Length > ReportRedactor.MaximumInputLength ? ReportRedactor.Mask : result;
        }
        catch (RegexMatchTimeoutException)
        {
            return ReportRedactor.Mask;
        }
    }
}
