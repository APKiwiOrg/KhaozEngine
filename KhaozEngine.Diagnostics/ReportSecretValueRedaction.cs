using System;
using System.Text;
using System.Text.RegularExpressions;

namespace KhaozEngine.Diagnostics;

/// <summary>Recognizes secret key prefixes, then scans their values without growing a combined regex automaton.</summary>
internal sealed class ReportSecretValueRedaction(TimeSpan timeout)
{
    readonly Regex prefix = new(
        @"(?:password|passwd|pwd|secret|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key|client[_-]?secret|connection[_-]?string|accountkey|sharedaccesskey)[""']?\s*[:=]\s*[""']?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, timeout);

    internal Regex Pattern => prefix;

    internal string Redact(string text)
    {
        if (text.Length > ReportRedactor.MaximumInputLength) return ReportRedactor.Mask;
        StringBuilder? output = null;
        int search = 0;
        int copied = 0;
        while (search < text.Length)
        {
            Regex.ValueMatchEnumerator matches = prefix.EnumerateMatches(text.AsSpan(search));
            if (!matches.MoveNext()) break;
            int valueStart = search + matches.Current.Index + matches.Current.Length;
            int valueEnd = valueStart;
            while (valueEnd < text.Length && IsValueCharacter(text[valueEnd])) valueEnd++;
            search = valueEnd;
            if (valueStart == valueEnd) continue;

            output ??= new StringBuilder(text.Length);
            int kept = valueStart - copied;
            if ((long)output.Length + kept + ReportRedactor.Mask.Length > ReportRedactor.MaximumInputLength)
                return ReportRedactor.Mask;
            output.Append(text, copied, kept).Append(ReportRedactor.Mask);
            copied = valueEnd;
        }

        if (output is null) return text;
        int tail = text.Length - copied;
        if ((long)output.Length + tail > ReportRedactor.MaximumInputLength) return ReportRedactor.Mask;
        return output.Append(text, copied, tail).ToString();
    }

    // The original value class was [^\s"',;&]+. Char.IsWhiteSpace and regex \s use the same Unicode set.
    static bool IsValueCharacter(char value) => !char.IsWhiteSpace(value) && value is not ('"' or '\'' or ',' or ';' or '&');
}
