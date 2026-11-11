using System;
using System.Text.Json;

namespace KhaozEngine;

/// <summary>Bounded property-name diagnostics shared by strict JSON readers.</summary>
internal static class JsonMemberDiagnostics
{
    internal const int NameLimit = 64;

    /// <summary>Returns the complete decoded name, or null when it exceeds the UTF-16 code-unit budget.
    /// Invalid text keeps the reader's decoding exception for the owning refusal family.</summary>
    internal static string? ReadName(ref Utf8JsonReader reader)
    {
        // Six encoded bytes per UTF-16 code unit is the maximum for JSON Unicode escapes.
        if (reader.ValueSpan.Length > 6 * NameLimit) return null;
        Span<char> name = stackalloc char[NameLimit];
        try
        {
            int length = reader.CopyString(name);
            return new string(name[..length]);
        }
        catch (ArgumentException)
        {
            // A bounded destination cannot hold the complete decoded name. Never publish a prefix.
            return null;
        }
    }
}
