using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// How a text value is rendered into an audit column capped at <see cref="ContentAuditEntry.MaxValueLength"/>
/// characters. A value that fits is written as it is. A longer one keeps its longest prefix that ends on a
/// whole character and gains the same visible <c>[cut]</c> marker every other abbreviated audit value carries,
/// so a reader never takes an abbreviated value for a complete one and never meets half a surrogate pair.
/// <para>
/// The audit is presentation. A published value stays recoverable in full from versioned text history, and a
/// pending value is held in full by the draft. Neither is ever reconstructed from an audit row.
/// </para>
/// </summary>
public static class ContentTextAuditRendering
{
    /// <summary>The visible marker an abbreviated value ends with.</summary>
    public const string CutMarker = "[cut]";

    /// <summary>The value as an audit column holds it, or null for an absent value.</summary>
    /// <param name="value">The complete value, or null when the string is absent.</param>
    public static string? Render(string? value)
    {
        if (value is null || value.Length <= ContentAuditEntry.MaxValueLength)
        {
            return value;
        }

        int keep = ContentAuditEntry.MaxValueLength - CutMarker.Length;
        if (char.IsHighSurrogate(value[keep - 1]))
        {
            keep--;
        }

        return string.Concat(value.AsSpan(0, keep), CutMarker);
    }
}
