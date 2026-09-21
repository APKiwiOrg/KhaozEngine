using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog;

/// <summary>
/// The positional row walk of spec 7.3, written once and driven by the type's schema, so a content type's
/// own file carries its field list and whatever constraint the generic walk cannot express and nothing
/// else.
/// <para>
/// A row body opens with the row's own KEY, a varint length then that many UTF-8 bytes, and every schema
/// field follows it IN DECLARED ORDER. The key is first because the runtime reads a key out of the row
/// bodies rather than out of a second array (spec 9.1), so an id-to-key read is the same slice one varint
/// in.
/// </para>
/// <para>
/// <b>The absence convention, which the encoder and the decoder must never disagree about.</b> Every field
/// occupies its position, and an ABSENT field writes the ZERO FORM of its kind: a zero varint for an int, a
/// scaled int or a key reference, a zero byte for a bool, a zero count for a tag list and a zero length for
/// opaque bytes. That is a single <c>00</c> byte in every case. A derived marker writes NOTHING at all, and
/// is the one field that takes no width in the sequence. There is no presence bit and no absence sentinel,
/// which spec 7.9 forces rather than merely permits: its worked <c>tag</c> row spends exactly one byte on
/// the optional <c>sort</c> field, so a presence byte would make that golden seven bytes where the spec
/// says six plus one.
/// </para>
/// <para>
/// The price is that absence and zero share an encoding, so a field the author left empty and a field the
/// author set to zero are the same bytes. The decoder resolves that ONE way: an OPTIONAL field whose zero
/// form is read comes back <see cref="ContentFieldValue.IsAbsent"/>, and a REQUIRED field always comes back
/// carrying its value, because a required <c>value</c> of 0 or a required <c>stackable</c> of false are
/// ordinary rows and reporting them as absent would turn every free item into a <c>KEC0005</c> finding.
/// </para>
/// <para>
/// Every decode path is TOTAL. The bytes arrive from a remote peer, so a malformed body returns false with
/// a stable reason token and never throws. The ENCODE path is ours, so it throws on a row that does not
/// match its schema: the validator's findings catch that earlier in a publish, and the throw is the proof
/// the check held rather than the way it is normally reported.
/// </para>
/// </summary>
public abstract class ContentRowCodecBase : IContentRowCodec
{
    /// <summary>A field's bytes do not match the shape its kind declares, contracts 9.7.</summary>
    public const string ReasonFieldMalformed = "field-malformed";

    /// <summary>A field's declared length runs past the end of the body, contracts 9.7.</summary>
    public const string ReasonFieldTruncated = "field-truncated";

    /// <summary>The largest an int-domain field is on the wire, which is the width of an <c>int</c>.</summary>
    const int MaxVarint32Bytes = 5;

    /// <summary>Builds a codec over one content type's schema.</summary>
    /// <param name="type">The type id the decoded rows carry.</param>
    /// <param name="schema">The ordered field list the walk is driven by.</param>
    protected ContentRowCodecBase(ContentTypeId type, ContentFieldSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        Type = type;
        Schema = schema;

        var written = new List<string>(schema.Fields.Count);
        foreach (ContentFieldEntry field in schema.Fields)
        {
            if (!field.IsDerivedMarker)
            {
                written.Add(field.Name);
            }
        }

        WrittenFields = written;
    }

    /// <summary>The content type this codec belongs to, and the type every decoded row carries.</summary>
    public ContentTypeId Type { get; }

    /// <summary>The schema the positional walk follows.</summary>
    public ContentFieldSchema Schema { get; }

    /// <inheritdoc />
    /// <remarks>Every schema field except a derived marker, which no codec ever claims to write.</remarks>
    public IReadOnlyList<string> WrittenFields { get; }

    /// <summary>
    /// Builds a tag list value from ids in AUTHORED ORDER, contracts 4.6. The bytes are the varint ids with
    /// no count of their own, because the count is structure the row walk writes rather than part of the
    /// value.
    /// </summary>
    public static ContentFieldValue TagListValue(IReadOnlyList<int> tagIds)
    {
        ArgumentNullException.ThrowIfNull(tagIds);

        if (tagIds.Count == 0)
        {
            return ContentFieldValue.Absent(ContentFieldKind.TagList);
        }

        Span<byte> scratch = stackalloc byte[MaxVarint32Bytes];
        var bytes = new List<byte>(tagIds.Count);
        foreach (int id in tagIds)
        {
            int written = ContentVarint.Write(scratch, unchecked((uint)id));
            for (int i = 0; i < written; i++)
            {
                bytes.Add(scratch[i]);
            }
        }

        return ContentFieldValue.OfBytes(ContentFieldKind.TagList, bytes.ToArray());
    }

    /// <summary>
    /// Reads tag ids from a tag-list value into <paramref name="destination"/> in authored order. Reading
    /// stops when the destination is full or the value reaches a malformed varint. Any ids already written
    /// remain available to the caller.
    /// </summary>
    /// <returns>The number of ids written.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a tag-list value.</exception>
    public static int ReadTagList(in ContentFieldValue value, Span<int> destination)
    {
        if (value.Kind != ContentFieldKind.TagList)
        {
            throw new ArgumentException("The field value is not a tag list.", nameof(value));
        }

        ReadOnlySpan<byte> bytes = value.Bytes.Span;
        int offset = 0;
        int written = 0;
        while (offset < bytes.Length && written < destination.Length)
        {
            if (!ContentVarint.TryRead(bytes, ref offset, out uint raw, out _))
            {
                break;
            }

            int tagId = unchecked((int)raw);
            if (tagId >= 1)
            {
                destination[written++] = tagId;
            }
        }

        return written;
    }

    /// <inheritdoc />
    public void Encode(ContentRow row, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(destination);

        CheckRowAgainstSchema(row);
        int size = Measure(row);
        Span<byte> span = destination.GetSpan(size);
        WriteRow(row, span);
        destination.Advance(size);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The body carries the key and the fields and nothing else, so the decoded row takes id 0, parent 0
    /// and a live retired bit. A chunk's row TABLE owns the id and the retired flag (spec 7.3) and the
    /// chunk decoder is what puts them back on.
    /// <para>
    /// A body that ENDS before the schema does is a row written under an earlier field list, and
    /// <see cref="ContentRowTailRule"/> is the one rule that covers it: the body may run out at or after the
    /// schema's baseline and never inside it. A body that runs PAST the schema is still
    /// <see cref="ReasonFieldMalformed"/>: those bytes describe fields this reader has no list for, so it
    /// cannot know what it would be discarding.
    /// </para>
    /// </remarks>
    public bool TryDecode(ReadOnlySpan<byte> body, [MaybeNullWhen(false)] out ContentRow row, out string? reason)
    {
        row = null;
        int offset = 0;
        if (!ContentVarint.TryRead(body, ref offset, out uint keyLength, out reason))
        {
            return false;
        }

        if (keyLength > (uint)(body.Length - offset))
        {
            reason = ReasonFieldTruncated;
            return false;
        }

        byte[] key = body.Slice(offset, (int)keyLength).ToArray();
        offset += (int)keyLength;

        var values = new ContentFieldValue[Schema.Fields.Count];
        for (int i = 0; i < values.Length; i++)
        {
            if (offset == body.Length && ContentRowTailRule.MayEndAt(Schema, i))
            {
                FillAbsentFrom(values, i);
                break;
            }

            if (!TryReadField(body, ref offset, i, Schema.Fields[i], out values[i], out reason))
            {
                return false;
            }
        }

        if (offset != body.Length)
        {
            reason = ReasonFieldMalformed;
            return false;
        }

        row = new ContentRow(Type, 0, new ContentKey(key, 0, key.Length), 0, false, values);
        reason = null;
        return true;
    }

    /// <summary>
    /// A constraint the generic walk cannot express, checked on BOTH sides so an encoder cannot write a row
    /// its own decoder refuses. Returns null when the value is fine, or a reason token otherwise. The
    /// default accepts everything the kind itself allows.
    /// </summary>
    /// <param name="fieldIndex">The field's index in the schema, which is its index in the row.</param>
    /// <param name="field">The schema entry being checked.</param>
    /// <param name="value">The value, never absent and never a derived marker.</param>
    protected virtual string? CheckFieldValue(int fieldIndex, ContentFieldEntry field, in ContentFieldValue value)
        => null;

    /// <summary>
    /// The body ended at a field <see cref="ContentRowTailRule"/> allows it to end at, so the rest of the
    /// schema is an APPEND this row predates and every remaining field comes back absent. Nothing can be
    /// refused here: an appended field is optional by construction, which
    /// <see cref="ContentFieldSchema"/> enforces at declaration rather than at every decode.
    /// </summary>
    /// <param name="values">The value array being filled, which this completes.</param>
    /// <param name="from">The first field the body had no bytes left for.</param>
    void FillAbsentFrom(ContentFieldValue[] values, int from)
    {
        for (int i = from; i < values.Length; i++)
        {
            values[i] = ContentFieldValue.Absent(Schema.Fields[i].Kind);
        }
    }

    void CheckRowAgainstSchema(ContentRow row)
    {
        if (row.Fields.Count != Schema.Fields.Count)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"Type '{Type.Value}' has {Schema.Fields.Count} schema fields and the row carries {row.Fields.Count} values, which a positional walk cannot reconcile."),
                nameof(row));
        }

        for (int i = 0; i < Schema.Fields.Count; i++)
        {
            ContentFieldEntry field = Schema.Fields[i];
            ContentFieldValue value = row.Fields[i];
            if (field.IsDerivedMarker)
            {
                if (!value.IsAbsent)
                {
                    throw new ArgumentException(
                        FormattableString.Invariant(
                            $"Field '{field.Name}' is a derived marker carrying no value, and the row hands one in."),
                        nameof(row));
                }

                continue;
            }

            if (value.IsAbsent)
            {
                continue;
            }

            if (value.Kind != field.Kind)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Field '{field.Name}' is a {field.Kind} and the row carries a {value.Kind} for it."),
                    nameof(row));
            }

            CheckValueFitsItsKind(field, value);
            string? reason = CheckFieldValue(i, field, value);
            if (reason is not null)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Field '{field.Name}' carries a value this type refuses, '{reason}', so encoding it would write a row its own decoder rejects."),
                    nameof(row));
            }
        }
    }

    static void CheckValueFitsItsKind(ContentFieldEntry field, in ContentFieldValue value)
    {
        switch (field.Kind)
        {
            case ContentFieldKind.Int:
            case ContentFieldKind.ScaledInt:
            case ContentFieldKind.KeyReference:
                if (value.Number is < int.MinValue or > int.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value),
                        value.Number,
                        FormattableString.Invariant(
                            $"Field '{field.Name}' is written as a 32 bit varint, which is contracts 5.1's id width, so its value has to fit an int."));
                }

                break;
            case ContentFieldKind.Bool:
                if (value.Number is not (0 or 1))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value),
                        value.Number,
                        FormattableString.Invariant(
                            $"Field '{field.Name}' is a bool and carries {value.Number}. The format is canonical, so a bool is 0 or 1 and nothing else."));
                }

                break;
            case ContentFieldKind.TagList:
                if (CountTags(value.Bytes.Span) < 0)
                {
                    throw new ArgumentException(
                        FormattableString.Invariant(
                            $"Field '{field.Name}' carries tag list bytes that are not a run of minimal varints."),
                        nameof(value));
                }

                break;
            default:
                break;
        }
    }

    int Measure(ContentRow row)
    {
        ReadOnlySpan<byte> key = row.Key.Utf8;
        int total = ContentVarint.Size((uint)key.Length) + key.Length;
        int written = ContentRowTailRule.WrittenFieldCount(Schema, row.Fields);
        for (int i = 0; i < written; i++)
        {
            ContentFieldEntry field = Schema.Fields[i];
            ContentFieldValue value = row.Fields[i];
            if (field.IsDerivedMarker)
            {
                continue;
            }

            if (value.IsAbsent)
            {
                total++;
                continue;
            }

            total += field.Kind switch
            {
                ContentFieldKind.Bool => 1,
                ContentFieldKind.TagList
                    => ContentVarint.Size((uint)CountTags(value.Bytes.Span)) + value.Bytes.Length,
                ContentFieldKind.OpaqueBytes
                    => ContentVarint.Size((uint)value.Bytes.Length) + value.Bytes.Length,
                _ => ContentVarint.Size(unchecked((uint)(int)value.Number)),
            };
        }

        return total;
    }

    /// <summary>
    /// Writes the key then one entry per non-marker field, positionally, in schema order.
    /// <para>
    /// <b><c>Int</c> and <c>ScaledInt</c> go out as UNSIGNED two's-complement varints, not zig-zag</b>, which
    /// is what design 7.9 specifies and what the goldens record: its worked example writes the tag's
    /// <c>sort: varint 10</c> as the single byte <c>0A</c>. A negative value therefore costs five bytes,
    /// which is deliberate and which no catalog field pays, because the zig-zag rule of contracts 15 applies
    /// only to a field DECLARED signed and the catalog schema declares none. Changing this is a format
    /// change and a re-bake of every golden, not a codec tidy-up.
    /// </para>
    /// <para>
    /// The field COUNT it writes is <see cref="ContentRowTailRule.WrittenFieldCount"/> rather than the whole
    /// schema, which is what makes the form canonical after a type gains a field: a row that sets none of the
    /// appended fields goes out as the bytes the type's first release wrote.
    /// </para>
    /// </summary>
    void WriteRow(ContentRow row, Span<byte> destination)
    {
        ReadOnlySpan<byte> key = row.Key.Utf8;
        int written = ContentVarint.Write(destination, (uint)key.Length);
        key.CopyTo(destination[written..]);
        written += key.Length;

        int fields = ContentRowTailRule.WrittenFieldCount(Schema, row.Fields);
        for (int i = 0; i < fields; i++)
        {
            ContentFieldEntry field = Schema.Fields[i];
            ContentFieldValue value = row.Fields[i];
            if (field.IsDerivedMarker)
            {
                continue;
            }

            if (value.IsAbsent)
            {
                destination[written++] = 0;
                continue;
            }

            switch (field.Kind)
            {
                case ContentFieldKind.Bool:
                    destination[written++] = (byte)value.Number;
                    break;
                case ContentFieldKind.TagList:
                    written += ContentVarint.Write(destination[written..], (uint)CountTags(value.Bytes.Span));
                    value.Bytes.Span.CopyTo(destination[written..]);
                    written += value.Bytes.Length;
                    break;
                case ContentFieldKind.OpaqueBytes:
                    written += ContentVarint.Write(destination[written..], (uint)value.Bytes.Length);
                    value.Bytes.Span.CopyTo(destination[written..]);
                    written += value.Bytes.Length;
                    break;
                default:
                    written += ContentVarint.Write(destination[written..], unchecked((uint)(int)value.Number));
                    break;
            }
        }
    }

    bool TryReadField(
        ReadOnlySpan<byte> body,
        ref int offset,
        int index,
        ContentFieldEntry field,
        out ContentFieldValue value,
        out string? reason)
    {
        value = ContentFieldValue.Absent(field.Kind);
        reason = null;
        if (field.IsDerivedMarker)
        {
            return true;
        }

        switch (field.Kind)
        {
            case ContentFieldKind.Bool:
                if (offset >= body.Length)
                {
                    reason = ReasonFieldTruncated;
                    return false;
                }

                byte flag = body[offset++];
                if (flag > 1)
                {
                    reason = ReasonFieldMalformed;
                    return false;
                }

                if (flag != 0 || field.Required)
                {
                    value = ContentFieldValue.OfNumber(ContentFieldKind.Bool, flag);
                }

                break;
            case ContentFieldKind.TagList:
                if (!TryReadTagList(body, ref offset, field, out value, out reason))
                {
                    return false;
                }

                break;
            case ContentFieldKind.OpaqueBytes:
                if (!TryReadOpaque(body, ref offset, field, out value, out reason))
                {
                    return false;
                }

                break;
            default:
                if (!ContentVarint.TryRead(body, ref offset, out uint raw, out reason))
                {
                    return false;
                }

                if (raw != 0 || field.Required)
                {
                    value = ContentFieldValue.OfNumber(field.Kind, unchecked((int)raw));
                }

                break;
        }

        if (value.IsAbsent)
        {
            return true;
        }

        reason = CheckFieldValue(index, field, value);
        if (reason is null)
        {
            return true;
        }

        value = ContentFieldValue.Absent(field.Kind);
        return false;
    }

    static bool TryReadTagList(
        ReadOnlySpan<byte> body,
        ref int offset,
        ContentFieldEntry field,
        out ContentFieldValue value,
        out string? reason)
    {
        value = ContentFieldValue.Absent(field.Kind);
        if (!ContentVarint.TryRead(body, ref offset, out uint count, out reason))
        {
            return false;
        }

        int start = offset;
        for (uint i = 0; i < count; i++)
        {
            if (!ContentVarint.TryRead(body, ref offset, out _, out reason))
            {
                return false;
            }
        }

        if (count != 0 || field.Required)
        {
            value = ContentFieldValue.OfBytes(ContentFieldKind.TagList, body[start..offset].ToArray());
        }

        return true;
    }

    static bool TryReadOpaque(
        ReadOnlySpan<byte> body,
        ref int offset,
        ContentFieldEntry field,
        out ContentFieldValue value,
        out string? reason)
    {
        value = ContentFieldValue.Absent(field.Kind);
        if (!ContentVarint.TryRead(body, ref offset, out uint length, out reason))
        {
            return false;
        }

        if (length > (uint)(body.Length - offset))
        {
            reason = ReasonFieldTruncated;
            return false;
        }

        if (length != 0 || field.Required)
        {
            value = ContentFieldValue.OfBytes(ContentFieldKind.OpaqueBytes, body.Slice(offset, (int)length).ToArray());
        }

        offset += (int)length;
        return true;
    }

    /// <summary>The tag ids in a tag list value's bytes, or -1 when they are not a run of minimal varints.</summary>
    static int CountTags(ReadOnlySpan<byte> bytes)
    {
        int offset = 0;
        int count = 0;
        while (offset < bytes.Length)
        {
            if (!ContentVarint.TryRead(bytes, ref offset, out _, out _))
            {
                return -1;
            }

            count++;
        }

        return count;
    }
}
