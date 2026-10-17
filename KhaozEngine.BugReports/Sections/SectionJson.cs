using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports;

/// <summary>The JSON passes behind <see cref="BugReportSectionSet"/>: error objects, redaction and structural truncation.</summary>
static class SectionJson
{
    const string ErrorName = "error";
    const string TruncatedName = "truncated";

    internal static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        MaxDepth = BugReportLimits.MaxSectionDepth,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = BugReportLimits.MaxSectionDepth };

    static readonly byte[] TruncatedMarker = "{\"truncated\":true}"u8.ToArray();

    /// <summary><c>{"error":"&lt;full type name&gt;: &lt;message&gt;"}</c> with the message cut to <paramref name="maxMessage"/> units.</summary>
    internal static byte[] ErrorObject(Exception exception, int maxMessage)
    {
        string message;
        try
        {
            message = exception.Message;
        }
        catch (Exception)
        {
            message = "";
        }
        return ErrorObject(ErrorName, exception.GetType().FullName + ": " + Cut(message, maxMessage));
    }

    /// <summary>
    /// <paramref name="text"/> cut to at most <paramref name="maxUnits"/> UTF-16 units without splitting a surrogate
    /// pair. Returns the same instance when it already fits.
    /// </summary>
    internal static string Cut(string text, int maxUnits)
    {
        if (text.Length <= maxUnits) return text;
        int keep = maxUnits > 0 && char.IsHighSurrogate(text[maxUnits - 1]) ? maxUnits - 1 : maxUnits;
        return text[..keep];
    }

    /// <summary>
    /// Re-tokenises one JSON object, passing every property name and string value through <paramref name="redact"/>.
    /// A name that collides with an earlier name in its object after redaction gets the smallest free <c>#n</c>
    /// suffix from 2 upward. Returns null when <paramref name="json"/> is not exactly one JSON object or holds a
    /// string that cannot be read as UTF-16.
    /// </summary>
    internal static byte[]? Redact(ReadOnlySpan<byte> json, Func<string, string> redact)
    {
        var output = new ArrayBufferWriter<byte>(Math.Max(json.Length, 16));
        try
        {
            var reader = new Utf8JsonReader(json, ReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            using var writer = new Utf8JsonWriter(output, WriterOptions);
            var names = new Stack<UniqueNames?>();
            do
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        writer.WriteStartObject();
                        names.Push(new UniqueNames());
                        break;
                    case JsonTokenType.StartArray:
                        writer.WriteStartArray();
                        names.Push(null);
                        break;
                    case JsonTokenType.EndObject:
                        writer.WriteEndObject();
                        names.Pop();
                        break;
                    case JsonTokenType.EndArray:
                        writer.WriteEndArray();
                        names.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        writer.WritePropertyName(names.Peek()!.Claim(redact(reader.GetString()!)));
                        break;
                    case JsonTokenType.String:
                        writer.WriteStringValue(redact(reader.GetString()!));
                        break;
                    default:
                        WriteLiteral(writer, ref reader);
                        break;
                }
            }
            while (reader.Read());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // GetString throws InvalidOperationException for an escaped lone surrogate or invalid UTF-8.
            return null;
        }
        return output.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Cuts the message of an error object to <paramref name="maxMessage"/> UTF-16 units, scalar-safe. The message is
    /// whatever follows the first <c>": "</c> of the root's first string. Returns the input when nothing changes.
    /// </summary>
    internal static byte[] CutErrorMessage(byte[] json, int maxMessage)
    {
        var reader = new Utf8JsonReader(json, ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return json;
        if (!reader.Read() || reader.TokenType != JsonTokenType.PropertyName) return json;
        string name = reader.GetString()!;
        if (!reader.Read() || reader.TokenType != JsonTokenType.String) return json;
        string text = reader.GetString()!;
        int split = text.IndexOf(": ", StringComparison.Ordinal);
        int start = split < 0 ? 0 : split + 2;
        string message = text[start..];
        string cut = Cut(message, maxMessage);
        if (cut.Length == message.Length) return json;
        return ErrorObject(name, text[..start] + cut);
    }

    /// <summary>
    /// <paramref name="json"/> when it fits <paramref name="budget"/>. Otherwise the largest prefix that fits: the
    /// root opens with <c>"truncated":true</c>, leaf values follow in document order, and every open container is
    /// closed. <c>{"truncated":true}</c> when not even the containers before the first leaf fit.
    /// </summary>
    internal static byte[] Truncate(byte[] json, int budget)
    {
        if (json.Length <= budget) return json;
        var output = new ArrayBufferWriter<byte>(Math.Min(budget, json.Length) + 64);
        if (!Copy(json, 0, output, budget)) return (byte[])TruncatedMarker.Clone();

        int low = 0;
        int high = CountLeaves(json);
        while (low < high)
        {
            int middle = low + (high - low + 1) / 2;
            if (Copy(json, middle, output, budget)) low = middle;
            else high = middle - 1;
        }
        Copy(json, low, output, budget);
        return output.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Writes the truncated form of <paramref name="json"/> holding its first <paramref name="leaves"/> leaf values
    /// into <paramref name="output"/>, cleared first. False as soon as the result would exceed <paramref name="limit"/>.
    /// </summary>
    static bool Copy(ReadOnlySpan<byte> json, int leaves, ArrayBufferWriter<byte> output, int limit)
    {
        output.Clear();
        var reader = new Utf8JsonReader(json, ReaderOptions);
        reader.Read();
        using var writer = new Utf8JsonWriter(output, WriterOptions);
        writer.WriteStartObject();
        writer.WriteBoolean(TruncatedName, true);

        var rootNames = new UniqueNames();
        rootNames.Claim(TruncatedName);
        var open = new Stack<bool>();
        open.Push(true);
        string? pending = null;
        int copied = 0;
        while (reader.Read())
        {
            JsonTokenType token = reader.TokenType;
            if (token == JsonTokenType.PropertyName)
            {
                pending = reader.GetString()!;
                continue;
            }
            if (token is JsonTokenType.EndObject or JsonTokenType.EndArray)
            {
                if (open.Count == 1) break;
                open.Pop();
                if (token == JsonTokenType.EndObject) writer.WriteEndObject();
                else writer.WriteEndArray();
            }
            else
            {
                bool container = token is JsonTokenType.StartObject or JsonTokenType.StartArray;
                if (!container && copied == leaves) break;
                if (pending is not null)
                {
                    writer.WritePropertyName(open.Count == 1 ? rootNames.Claim(pending) : pending);
                    pending = null;
                }
                if (token == JsonTokenType.StartObject)
                {
                    writer.WriteStartObject();
                    open.Push(true);
                }
                else if (token == JsonTokenType.StartArray)
                {
                    writer.WriteStartArray();
                    open.Push(false);
                }
                else
                {
                    WriteLiteral(writer, ref reader);
                    copied++;
                }
            }
            if (writer.BytesCommitted + writer.BytesPending + open.Count > limit) return false;
        }

        while (open.Count > 0)
        {
            if (open.Pop()) writer.WriteEndObject();
            else writer.WriteEndArray();
        }
        writer.Flush();
        return output.WrittenCount <= limit;
    }

    static int CountLeaves(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, ReaderOptions);
        int count = 0;
        while (reader.Read())
        {
            if (reader.TokenType is JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True
                or JsonTokenType.False or JsonTokenType.Null)
                count++;
        }
        return count;
    }

    static void WriteLiteral(Utf8JsonWriter writer, ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                writer.WriteStringValue(reader.GetString());
                break;
            case JsonTokenType.Number:
                writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                break;
            case JsonTokenType.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonTokenType.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    static byte[] ErrorObject(string name, string text)
    {
        var output = new ArrayBufferWriter<byte>(text.Length + 16);
        using (var writer = new Utf8JsonWriter(output, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString(name, text);
            writer.WriteEndObject();
        }
        return output.WrittenSpan.ToArray();
    }

    /// <summary>The property names of one object, renaming a repeat to the smallest free <c>name#n</c>.</summary>
    sealed class UniqueNames
    {
        readonly HashSet<string> taken = new(StringComparer.Ordinal);
        Dictionary<string, int>? nextSuffix;

        public string Claim(string name)
        {
            if (taken.Add(name)) return name;
            nextSuffix ??= new Dictionary<string, int>(StringComparer.Ordinal);
            // Names are only ever added, so every suffix below the remembered one is still taken.
            int n = nextSuffix.TryGetValue(name, out int remembered) ? remembered : 2;
            string candidate;
            while (!taken.Add(candidate = name + "#" + n.ToString(CultureInfo.InvariantCulture))) n++;
            nextSuffix[name] = n + 1;
            return candidate;
        }
    }
}
