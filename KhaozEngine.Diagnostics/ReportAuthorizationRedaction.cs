using System;
using System.Text;
using System.Text.RegularExpressions;

namespace KhaozEngine.Diagnostics;

/// <summary>Preserves the authorization token spans while rejecting Basic candidates without decoded credentials.</summary>
internal static class ReportAuthorizationRedaction
{
    internal static string Redact(string text, Regex pattern)
    {
        if (text.Length > ReportRedactor.MaximumInputLength) return ReportRedactor.Mask;
        StringBuilder? output = null;
        int copied = 0;
        foreach (ValueMatch match in pattern.EnumerateMatches(text.AsSpan()))
        {
            int schemeEnd = match.Index;
            while (!char.IsWhiteSpace(text[schemeEnd])) schemeEnd++;
            int valueStart = schemeEnd;
            while (char.IsWhiteSpace(text[valueStart])) valueStart++;
            int valueEnd = match.Index + match.Length;
            bool basic = schemeEnd - match.Index == "basic".Length;
            if (basic && !HasCredentialPrefix(text.AsSpan(valueStart, valueEnd - valueStart))) continue;

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

    static bool HasCredentialPrefix(ReadOnlySpan<char> token)
    {
        Span<byte> decoded = stackalloc byte[3];
        for (int offset = 0; offset <= token.Length - 4; offset += 4)
        {
            // Only leading complete quanta count. Invalid input or padding ends the decodable prefix,
            // while an incomplete trailing quantum cannot undo a user:password separator already found.
            if (!Convert.TryFromBase64Chars(token.Slice(offset, 4), decoded, out int written)) return false;
            if (decoded[..written].Contains((byte)':')) return true;
            if (written < 3) return false;
        }
        return false;
    }
}
