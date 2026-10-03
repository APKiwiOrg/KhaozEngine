using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Unicode;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>One language's text chunk regenerated for an exact recorded version.</summary>
/// <param name="Recorded">The language as the version recorded it, wire spelling and hash included.</param>
/// <param name="Hash">The regenerated content address.</param>
/// <param name="StoredFile">The regenerated stored file.</param>
internal sealed record ContentRegeneratedText(ContentTextLanguage Recorded, string Hash, byte[] StoredFile);

/// <summary>
/// Encodes one KECT chunk per declared language through the EXISTING codec and hash sub-domain, after a
/// strict producer preflight. Nothing here relies on the codec to refuse bad input: every derived key is
/// measured as strict UTF-8 against 192 bytes, every value against 8192, every wire tag against 35, the
/// entries are sorted ordinally over their UTF-8 key bytes, a repeated derived key is refused, and each
/// language's uncompressed body is measured with checked arithmetic, entry-count and length varints
/// included, against the 16 MiB chunk ceiling BEFORE any canonical buffer is allocated.
/// <para>
/// <b>An unchanged language is reused, not encoded.</b> A baseline language whose values no close or insert
/// touches keeps its recorded hash and writes nothing, an established empty chunk included. A changed
/// language whose bytes come out identical is reused as well.
/// </para>
/// </summary>
internal static class ContentTextChunkBuilder
{
    /// <summary>One chunk per declared language, in the candidate's declaration order.</summary>
    /// <param name="registry">The registry keys are derived through.</param>
    /// <param name="candidate">The merged candidate.</param>
    /// <param name="liveRows">Every row live at the new version, which supplies each value's content key.</param>
    /// <param name="baseline">The baseline text, whose hashes unchanged languages reuse.</param>
    /// <exception cref="ContentAuthoringException">A key, value, tag or body exceeds its bound, a value is ineligible or names no row, or two values derive one key.</exception>
    public static IReadOnlyList<ContentTextChunkRecord> Build(
        ContentTypeRegistry registry,
        ContentTextCandidate candidate,
        IReadOnlyList<ContentRowRevision> liveRows,
        ContentVersionTextSnapshot baseline)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(liveRows);
        ArgumentNullException.ThrowIfNull(baseline);

        var keys = new Dictionary<(ushort, int), ContentKey>(liveRows.Count);
        foreach (ContentRowRevision row in liveRows)
        {
            keys[(row.Row.Type.Value, row.Row.Id)] = row.Row.Key;
        }

        KeyValuePair<string, string>[][] entries = Prepare(registry, candidate.Declarations, candidate.Values, keys);

        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (ContentTextRevision close in candidate.Closes)
        {
            changed.Add(close.Language);
        }

        foreach (ContentTextRevision insert in candidate.Inserts)
        {
            changed.Add(insert.Language);
        }

        var recorded = new Dictionary<string, ContentTextLanguage>(StringComparer.Ordinal);
        foreach (ContentTextLanguage language in baseline.Languages)
        {
            recorded[language.Language] = language;
        }

        var chunks = new ContentTextChunkRecord[candidate.Declarations.Count];
        for (int i = 0; i < chunks.Length; i++)
        {
            ContentTextLanguageDeclaration declaration = candidate.Declarations[i];
            bool established = recorded.TryGetValue(declaration.Language, out ContentTextLanguage? held)
                && string.Equals(held.WireTag, declaration.WireTag, StringComparison.Ordinal);
            if (established && !changed.Contains(declaration.Language))
            {
                chunks[i] = new ContentTextChunkRecord(declaration.WireTag, held!.Hash, default, true);
                continue;
            }

            string hash = ContentTextChunkCodec.Hash(declaration.WireTag, entries[i]);
            chunks[i] = established && string.Equals(held!.Hash, hash, StringComparison.Ordinal)
                ? new ContentTextChunkRecord(declaration.WireTag, hash, default, true)
                : new ContentTextChunkRecord(
                    declaration.WireTag, hash, ContentTextChunkCodec.Encode(declaration.WireTag, entries[i]), false);
        }

        return chunks;
    }

    /// <summary>
    /// Every recorded language of one exact version, regenerated in its recorded wire spelling from that
    /// version's own values and rows, after the same preflight a publish runs.
    /// </summary>
    /// <param name="registry">The registry keys are derived through.</param>
    /// <param name="text">The version's complete text.</param>
    /// <param name="keys">The content key of every row live at the version, by type and id.</param>
    /// <exception cref="ContentAuthoringException">The values cannot be regenerated.</exception>
    public static IReadOnlyList<ContentRegeneratedText> Regenerate(
        ContentTypeRegistry registry,
        ContentVersionTextSnapshot text,
        IReadOnlyDictionary<(ushort, int), ContentKey> keys)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(keys);

        var declarations = new ContentTextLanguageDeclaration[text.Languages.Count];
        for (int i = 0; i < declarations.Length; i++)
        {
            declarations[i] = text.Languages[i].Declaration;
        }

        KeyValuePair<string, string>[][] entries = Prepare(registry, declarations, text.Revisions, keys);
        var regenerated = new ContentRegeneratedText[declarations.Length];
        for (int i = 0; i < regenerated.Length; i++)
        {
            string tag = declarations[i].WireTag;
            regenerated[i] = new ContentRegeneratedText(
                text.Languages[i], ContentTextChunkCodec.Hash(tag, entries[i]), ContentTextChunkCodec.Encode(tag, entries[i]));
        }

        return regenerated;
    }

    /// <summary>
    /// The preflight: every value's derived key and value measured, grouped by language, sorted ordinally over
    /// UTF-8 key bytes, refused on a repeated key, and every body measured against the ceiling, all before a
    /// single canonical buffer exists.
    /// </summary>
    static KeyValuePair<string, string>[][] Prepare(
        ContentTypeRegistry registry,
        IReadOnlyList<ContentTextLanguageDeclaration> declarations,
        IReadOnlyList<ContentTextRevision> values,
        IReadOnlyDictionary<(ushort, int), ContentKey> keys)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var groups = new List<Entry>[declarations.Count];
        for (int i = 0; i < declarations.Count; i++)
        {
            if (Encoding.ASCII.GetByteCount(declarations[i].WireTag) > ContentTextChunkCodec.MaxLanguageTagBytes)
            {
                throw Bounds(default, 0, FormattableString.Invariant(
                    $"Wire tag '{declarations[i].WireTag}' is over {ContentTextChunkCodec.MaxLanguageTagBytes} bytes."));
            }

            index[declarations[i].Language] = i;
            groups[i] = [];
        }

        foreach (ContentTextRevision value in values)
        {
            if (!index.TryGetValue(value.Language, out int language))
            {
                throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"Text of type {value.Type.Value} row {value.DefinitionId} is in language '{value.Language}', which the version does not declare."),
                    value.Type,
                    value.DefinitionId,
                    ContentAuthoringException.TextLanguageUndeclaredReason);
            }

            groups[language].Add(Derive(registry, value, keys));
        }

        var entries = new KeyValuePair<string, string>[declarations.Count][];
        for (int i = 0; i < groups.Length; i++)
        {
            List<Entry> group = groups[i];
            group.Sort(static (left, right) => left.KeyUtf8.AsSpan().SequenceCompareTo(right.KeyUtf8));
            long body = ContentVarint.Size(checked((uint)group.Count));
            var pairs = new KeyValuePair<string, string>[group.Count];
            for (int e = 0; e < group.Count; e++)
            {
                Entry entry = group[e];
                if (e > 0 && group[e - 1].KeyUtf8.AsSpan().SequenceEqual(entry.KeyUtf8))
                {
                    throw new ContentAuthoringException(
                        FormattableString.Invariant(
                            $"Two strings derive the key '{entry.Key}' in language '{declarations[i].WireTag}', so neither has a representable identity."),
                        default,
                        0,
                        ContentAuthoringException.EditTargetCollisionReason);
                }

                body = checked(body + 1 + entry.KeyUtf8.Length + ContentVarint.Size((uint)entry.ValueBytes) + entry.ValueBytes);
                pairs[e] = new KeyValuePair<string, string>(entry.Key, entry.Value);
            }

            if (body > ContentPackFormat.MaxChunkUncompressedBytes)
            {
                throw Bounds(default, 0, FormattableString.Invariant(
                    $"Language '{declarations[i].WireTag}' needs an uncompressed body of {body} bytes, over the {ContentPackFormat.MaxChunkUncompressedBytes} byte chunk ceiling. Nothing was encoded or written."));
            }

            entries[i] = pairs;
        }

        return entries;
    }

    /// <summary>One value's derived key and strict measurements.</summary>
    static Entry Derive(
        ContentTypeRegistry registry,
        ContentTextRevision value,
        IReadOnlyDictionary<(ushort, int), ContentKey> keys)
    {
        ContentTypeRegistration registration = ContentTextCandidateBuilder.RequireEligible(
            registry, ContentTextSlot.Of(value));
        if (!keys.TryGetValue((value.Type.Value, value.DefinitionId), out ContentKey contentKey))
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"Text of type {value.Type.Value} row {value.DefinitionId} names a row the version does not hold."),
                value.Type,
                value.DefinitionId,
                ContentAuthoringException.UnknownRowReason);
        }

        int keyBytes;
        int valueBytes;
        try
        {
            keyBytes = checked(
                ContentTextEdit.MeasureUtf8(registration.TypeKey, nameof(registration))
                + 2
                + contentKey.Utf8.Length
                + ContentTextEdit.MeasureUtf8(value.FieldName, nameof(value)));
            valueBytes = ContentTextEdit.MeasureUtf8(value.Value, nameof(value));
        }
        catch (ArgumentException invalid)
        {
            throw Bounds(value.Type, value.DefinitionId, invalid.Message);
        }

        if (!Utf8.IsValid(contentKey.Utf8) || keyBytes is < 1 or > ContentTextChunkCodec.MaxKeyBytes)
        {
            throw Bounds(value.Type, value.DefinitionId, FormattableString.Invariant(
                $"The derived key of type {value.Type.Value} row {value.DefinitionId} field '{value.FieldName}' is {keyBytes} bytes or not valid UTF-8, and a key is 1 to {ContentTextChunkCodec.MaxKeyBytes} strict UTF-8 bytes."));
        }

        if (valueBytes > ContentTextChunkCodec.MaxValueBytes)
        {
            throw Bounds(value.Type, value.DefinitionId, FormattableString.Invariant(
                $"The value of type {value.Type.Value} row {value.DefinitionId} field '{value.FieldName}' is {valueBytes} bytes, over {ContentTextChunkCodec.MaxValueBytes}."));
        }

        string key = ContentTextKey.Derive(registration.TypeKey, contentKey.Utf8, value.FieldName);
        byte[] keyUtf8 = Encoding.UTF8.GetBytes(key);
        return keyUtf8.Length == keyBytes
            ? new Entry(key, keyUtf8, value.Value, valueBytes)
            : throw Bounds(value.Type, value.DefinitionId, "The derived key does not re-encode to its measured bytes.");
    }

    static ContentAuthoringException Bounds(ContentTypeId type, int definitionId, string detail)
        => new(detail, type, definitionId, ContentAuthoringException.TextBoundsReason);

    /// <summary>One measured entry.</summary>
    sealed record Entry(string Key, byte[] KeyUtf8, string Value, int ValueBytes);
}
