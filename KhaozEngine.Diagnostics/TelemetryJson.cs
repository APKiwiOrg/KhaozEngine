using System.Globalization;
using System.Text;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// The small amount of JSON writing the telemetry files need, shared by the per-frame sample rows and the
/// session header so both escape and format the same way. Hand-rolled rather than taken from a serializer
/// because the per-frame path runs on every recorded frame and must not build a writer or a document per
/// line, and because both shapes are fixed and owned entirely by this package.
/// </summary>
internal static class TelemetryJson
{
    /// <summary>
    /// Append a JSON number, or the literal <c>null</c> for NaN and infinity (JSON has a literal for
    /// neither, and one bad channel value must not cost the reader the whole line).
    /// </summary>
    internal static void AppendNumber(StringBuilder sb, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) { sb.Append("null"); return; }
        sb.Append(value.ToString(CultureInfo.InvariantCulture)); // shortest round-trippable form
    }

    /// <summary>Append a quoted, escaped JSON string, or the literal <c>null</c> for a null value.</summary>
    internal static void AppendString(StringBuilder sb, string? value)
    {
        if (value is null) { sb.Append("null"); return; }
        sb.Append('"');
        AppendEscaped(sb, value);
        sb.Append('"');
    }

    /// <summary>Append <c>"key":</c>, escaped, ready for the value that follows it.</summary>
    internal static void AppendKey(StringBuilder sb, string key)
    {
        sb.Append('"');
        AppendEscaped(sb, key);
        sb.Append("\":");
    }

    /// <summary>Append <c>true</c>, <c>false</c>, or the literal <c>null</c> for an unset value.</summary>
    internal static void AppendBool(StringBuilder sb, bool? value) =>
        sb.Append(value is null ? "null" : value.Value ? "true" : "false");

    /// <summary>
    /// Append the escaped BODY of a JSON string, without the surrounding quotes. Null and empty both append
    /// nothing, which is what the callers above want inside a pair of quotes they wrote themselves.
    /// </summary>
    internal static void AppendEscaped(StringBuilder sb, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        foreach (char ch in value)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
    }
}
