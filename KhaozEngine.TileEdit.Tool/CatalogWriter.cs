using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using KhaozEngine.TileWorld;

namespace KhaozEngine.TileEdit;

/// <summary>Writes <c>collisionHeight</c> into the archetype entries of a hand-authored catalog file WITHOUT
/// re-serialising it.
///
/// <para>A catalog is authored text: its property order, indentation, line endings, comments and trailing commas
/// are the author's, and System.Text.Json can write none of the last two. So the file is read once with a
/// <see cref="Utf8JsonReader"/> to find byte offsets, and only the named entries are spliced. A new height goes
/// right after the entry's <c>collisionKind</c> (after its last property when it has none), separated by the
/// same comma and whitespace that precede that property, so a one-line entry gains <c>, "collisionHeight": 2.5</c>
/// and a one-property-per-line entry gains a line in its own indentation and line ending (after the anchor's line
/// when a <c>//</c> comment ends it, so the comment stays with the property it annotates). An existing height
/// has only its value text replaced. Every other byte, a byte order mark included, is copied through.</para>
///
/// <para>The number is the float's shortest invariant round-trip form (what <see cref="float.ToString(IFormatProvider)"/>
/// gives under <see cref="CultureInfo.InvariantCulture"/>, the same digits as the <c>R</c> format): it parses
/// back to the same float, never carries a decimal comma, and is valid JSON (an exponent form such as
/// <c>1E-05</c> included).</para></summary>
public static class CatalogWriter
{
    static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

    static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The text a height is written as: the shortest invariant form that parses back to the same
    /// float.</summary>
    public static string FormatHeight(float height) => height.ToString(CultureInfo.InvariantCulture);

    /// <summary>Returns <paramref name="catalog"/> (UTF-8 catalog JSON, JSONC tolerated) with each named
    /// archetype's <c>collisionHeight</c> set, every other byte kept. With nothing to set, the input comes back
    /// unchanged.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A height is not a finite number above 0, which the catalog
    /// loader would refuse.</exception>
    /// <exception cref="TileWorldException">The catalog is not readable JSON, does not define a named archetype,
    /// or defines one twice, or an entry carries two <c>collisionHeight</c> properties.</exception>
    public static byte[] SetCollisionHeights(byte[] catalog, IReadOnlyDictionary<string, float> heights)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(heights);
        foreach ((string id, float h) in heights)
        {
            if (!(float.IsFinite(h) && h > 0f))
                throw new ArgumentOutOfRangeException(nameof(heights), h,
                    $"archetype '{id}': collisionHeight {FormatHeight(h)} is not a finite number above 0.");
        }
        if (heights.Count == 0) return catalog;

        int start = catalog.AsSpan().StartsWith(Bom) ? Bom.Length : 0;
        List<Splice> splices;
        try
        {
            splices = FindSplices(catalog, start, heights);
        }
        catch (JsonException ex)
        {
            throw new TileWorldException($"catalog is not readable JSON: {ex.Message}", ex);
        }

        using var output = new MemoryStream(catalog.Length + 32 * splices.Count);
        int at = 0;
        foreach (Splice s in splices.OrderBy(s => s.Start))
        {
            output.Write(catalog, at, s.Start - at);
            output.Write(s.Text);
            at = s.End;
        }
        output.Write(catalog, at, catalog.Length - at);
        return output.ToArray();
    }

    /// <summary>The bytes <paramref name="path"/> would hold after
    /// <see cref="SetCollisionHeights(byte[], IReadOnlyDictionary{string, float})"/>, already checked to load
    /// through the engine's catalog loader (schema included), or null when nothing would change. Writes nothing,
    /// so a caller with several files can prepare all of them before writing any.</summary>
    /// <exception cref="TileWorldException">The file cannot be read, a named archetype is not in it, or the result
    /// would not load.</exception>
    public static byte[]? Prepare(string path, IReadOnlyDictionary<string, float> heights)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] before;
        try { before = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TileWorldException($"{path}: cannot read catalog. {ex.Message}", ex);
        }
        byte[] after;
        try { after = SetCollisionHeights(before, heights); }
        catch (TileWorldException ex) { throw new TileWorldException($"{path}: {ex.Message}", ex); }
        if (after.AsSpan().SequenceEqual(before)) return null;

        // The loader reads the file as text, a byte order mark dropped, so the check reads it the same way.
        int start = after.AsSpan().StartsWith(Bom) ? Bom.Length : 0;
        TileWorldCatalogs.LoadJson(Encoding.UTF8.GetString(after, start, after.Length - start), path);
        return after;
    }

    /// <summary>Sets the heights in the catalog file at <paramref name="path"/> and writes it back, or leaves the
    /// file untouched (not even rewritten) when no byte would change. Returns whether it wrote.</summary>
    /// <exception cref="TileWorldException">As <see cref="Prepare"/>. Nothing is written then.</exception>
    public static bool WriteCollisionHeights(string path, IReadOnlyDictionary<string, float> heights)
    {
        byte[]? after = Prepare(path, heights);
        if (after is null) return false;
        File.WriteAllBytes(path, after);
        return true;
    }

    readonly record struct Splice(int Start, int End, byte[] Text);

    // One property of an archetype entry, as offsets into the whole file: where the text before it starts (just
    // after the previous value, or after the opening brace), where its name starts and ends, and its value's span.
    readonly record struct Property(int GapStart, int NameStart, int NameEnd, int ValueStart, int ValueEnd);

    static List<Splice> FindSplices(byte[] catalog, int start, IReadOnlyDictionary<string, float> heights)
    {
        var splices = new List<Splice>();
        var found = new HashSet<string>(StringComparer.Ordinal);
        var reader = new Utf8JsonReader(catalog.AsSpan(start), ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new TileWorldException("a catalog is a JSON object");
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool archetypes = reader.ValueTextEquals("archetypes"u8);
            reader.Read();
            if (archetypes && reader.TokenType == JsonTokenType.StartArray)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.StartObject)
                        ReadArchetype(ref reader, catalog, start, heights, found, splices);
                    else
                        reader.Skip();
                }
            }
            else
            {
                reader.Skip();
            }
        }

        string[] missing = heights.Keys.Where(id => !found.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            throw new TileWorldException(
                $"the catalog does not define archetype {string.Join(", ", missing.Select(id => $"'{id}'"))}");
        return splices;
    }

    static void ReadArchetype(ref Utf8JsonReader reader, byte[] catalog, int start,
        IReadOnlyDictionary<string, float> heights, HashSet<string> found, List<Splice> splices)
    {
        string? id = null;
        Property? kind = null, height = null, last = null;
        bool twoHeights = false;
        int gapStart = start + (int)reader.TokenStartIndex + 1;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            int nameStart = start + (int)reader.TokenStartIndex;
            // ValueSpan is the name's raw text between its quotes, escapes and all, so the closing quote is here.
            int nameEnd = nameStart + reader.ValueSpan.Length + 2;
            bool isId = reader.ValueTextEquals("id"u8);
            bool isKind = reader.ValueTextEquals("collisionKind"u8);
            bool isHeight = reader.ValueTextEquals("collisionHeight"u8);
            reader.Read();
            int valueStart = start + (int)reader.TokenStartIndex;
            int valueEnd;
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
                valueEnd = start + (int)reader.TokenStartIndex + 1;
            }
            else
            {
                valueEnd = valueStart + reader.ValueSpan.Length + (reader.TokenType == JsonTokenType.String ? 2 : 0);
            }
            if (isId && reader.TokenType == JsonTokenType.String) id = reader.GetString();

            var property = new Property(gapStart, nameStart, nameEnd, valueStart, valueEnd);
            if (isKind) kind = property;
            if (isHeight)
            {
                twoHeights |= height is not null;
                height = property;
            }
            last = property;
            gapStart = valueEnd;
        }

        if (id is null || !heights.TryGetValue(id, out float value)) return;
        if (!found.Add(id)) throw new TileWorldException($"the catalog defines archetype '{id}' twice");
        // Which of the two a reader keeps is the reader's business, so neither is guessed at.
        if (twoHeights) throw new TileWorldException($"archetype '{id}' carries collisionHeight twice");
        byte[] number = Encoding.UTF8.GetBytes(FormatHeight(value));
        if (height is { } existing)
        {
            splices.Add(new Splice(existing.ValueStart, existing.ValueEnd, number));
            return;
        }
        // An entry always has properties (the schema requires id, name and meshRef), so last is set here.
        Property anchor = kind ?? last!.Value;
        bool hasNext = anchor != last!.Value;
        byte[] separator = Separator(catalog, anchor);
        byte[] added = Concat("\"collisionHeight\""u8.ToArray(), Colon(catalog, anchor), number);
        if (AfterLineComment(catalog, anchor, hasNext, separator, added, splices)) return;
        splices.Add(new Splice(anchor.ValueEnd, anchor.ValueEnd, Concat(separator, added)));
    }

    // A // comment that ends the anchor's line belongs to the anchor, so on a one-property-per-line entry the new
    // property goes on a line of its own AFTER that line, rather than between the value and its comment. The comma
    // the anchor needs is added before the comment when it has none. A comma-first layout (no comma after the
    // value, another property after it) is left to the plain insert, which stays valid there.
    static bool AfterLineComment(byte[] catalog, Property anchor, bool hasNext, byte[] separator, byte[] added,
        List<Splice> splices)
    {
        int lastNewline = Array.LastIndexOf(separator, (byte)'\n');
        if (lastNewline < 0) return false;
        int i = SkipSpaces(catalog, anchor.ValueEnd);
        bool comma = i < catalog.Length && catalog[i] == (byte)',';
        if (comma) i = SkipSpaces(catalog, i + 1);
        if (i + 1 >= catalog.Length || catalog[i] != (byte)'/' || catalog[i + 1] != (byte)'/') return false;
        if (!comma && hasNext) return false;
        int lineEnd = Array.IndexOf(catalog, (byte)'\n', i);
        if (lineEnd < 0) return false;

        byte[] eol = catalog[lineEnd - 1] == (byte)'\r' ? "\r\n"u8.ToArray() : "\n"u8.ToArray();
        byte[] indent = separator.AsSpan(lastNewline + 1).ToArray();
        if (!comma) splices.Add(new Splice(anchor.ValueEnd, anchor.ValueEnd, ","u8.ToArray()));
        splices.Add(new Splice(lineEnd + 1, lineEnd + 1,
            Concat(indent, added, hasNext ? ","u8.ToArray() : Array.Empty<byte>(), eol)));
        return true;
    }

    static int SkipSpaces(byte[] catalog, int i)
    {
        while (i < catalog.Length && catalog[i] is (byte)' ' or (byte)'\t') i++;
        return i;
    }

    // A comma, then the whitespace run that ends the gap before the anchor property: ", " on a one-line entry,
    // the line ending and indentation on a one-property-per-line one. Only the TRAILING run is taken, so a comment
    // or the comma itself in that gap is never copied.
    static byte[] Separator(byte[] catalog, Property anchor)
    {
        int i = anchor.NameStart;
        while (i > anchor.GapStart && IsWhitespace(catalog[i - 1])) i--;
        return Concat(","u8.ToArray(), catalog.AsSpan(i, anchor.NameStart - i).ToArray());
    }

    // The anchor's own text between its name and its value (": " or ":"), unless a comment sits there.
    static byte[] Colon(byte[] catalog, Property anchor)
    {
        ReadOnlySpan<byte> between = catalog.AsSpan(anchor.NameEnd, anchor.ValueStart - anchor.NameEnd);
        foreach (byte b in between)
            if (b != (byte)':' && !IsWhitespace(b)) return ": "u8.ToArray();
        return between.ToArray();
    }

    static bool IsWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
