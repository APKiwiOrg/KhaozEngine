using System;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.BugReports.Client;

/// <summary>
/// The bounds the client sections apply before they write, so no fact becomes one large string or an unbounded
/// list. A section's <see cref="IBugReportSection.MaxBytes"/> applies only after capture and redaction, so it cannot
/// stand in for these.
/// </summary>
internal static class ReportText
{
    /// <summary>The longest string or property name a section writes, in UTF-16 units.</summary>
    internal const int MaxUnits = 1024;

    /// <summary>The most injected modules the <c>graphics</c> section writes.</summary>
    internal const int MaxInjectedModules = 64;

    /// <summary>The most capabilities the <c>graphics</c> section writes.</summary>
    internal const int MaxCapabilities = 128;

    /// <summary>The most environment variables the <c>platform</c> section writes.</summary>
    internal const int MaxEnvironment = 64;

    const string Ellipsis = "...";

    /// <summary>
    /// <paramref name="text"/> when it fits <see cref="MaxUnits"/>, else its first 1021 units and <c>...</c>. A
    /// surrogate pair the cut would split is dropped whole, so the result is always well formed.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    internal static string? Bound(string? text)
    {
        if (text is null || text.Length <= MaxUnits) return text;
        int keep = MaxUnits - Ellipsis.Length;
        if (char.IsHighSurrogate(text[keep - 1])) keep--;
        return string.Concat(text.AsSpan(0, keep), Ellipsis);
    }
}
