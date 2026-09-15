using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog;

/// <summary>
/// One field's value on one row. It stores a NUMBER or BYTES, never both, chosen by the kind, and an
/// absent optional field carries <see cref="IsAbsent"/> rather than a sentinel number.
/// <para>
/// A <see cref="ContentFieldKind.LocalizedTextKey"/> marker is always absent, because the key is derived
/// from the type key, the content key and the field name and there is nowhere to put an authored one
/// (contracts 4.7). It still occupies a slot, which is what keeps the schema index and the row index the
/// same number.
/// </para>
/// </summary>
public readonly struct ContentFieldValue : IEquatable<ContentFieldValue>
{
    ContentFieldValue(ContentFieldKind kind, long number, ReadOnlyMemory<byte> bytes, bool isAbsent)
    {
        Kind = kind;
        Number = number;
        Bytes = bytes;
        IsAbsent = isAbsent;
    }

    /// <summary>The kind this value was written under, which is the schema entry's kind.</summary>
    public ContentFieldKind Kind { get; }

    /// <summary>
    /// The numeric payload of an <see cref="ContentFieldKind.Int"/>, a
    /// <see cref="ContentFieldKind.ScaledInt"/>, a <see cref="ContentFieldKind.Bool"/> (0 or 1) or a
    /// <see cref="ContentFieldKind.KeyReference"/>. Zero for every other kind.
    /// </summary>
    public long Number { get; }

    /// <summary>
    /// The byte payload of a <see cref="ContentFieldKind.TagList"/> (varint tag ids in authored order) or a
    /// <see cref="ContentFieldKind.OpaqueBytes"/>. Empty for every other kind.
    /// </summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>True when the row carries no value for this field, which a live required field may not be.</summary>
    public bool IsAbsent { get; }

    /// <summary>A field the row does not carry, and the only form a derived marker ever takes.</summary>
    public static ContentFieldValue Absent(ContentFieldKind kind) => new(kind, 0, default, true);

    /// <summary>A numeric value. Refuses a kind whose payload is bytes or nothing at all.</summary>
    /// <exception cref="ArgumentException">The kind does not store a number.</exception>
    public static ContentFieldValue OfNumber(ContentFieldKind kind, long value)
    {
        if (!StoresNumber(kind))
        {
            throw new ArgumentException(
                FormattableString.Invariant($"A {kind} field does not store a number."), nameof(kind));
        }

        return new ContentFieldValue(kind, value, default, false);
    }

    /// <summary>A byte value. Refuses a kind whose payload is a number or nothing at all.</summary>
    /// <exception cref="ArgumentException">The kind does not store bytes.</exception>
    public static ContentFieldValue OfBytes(ContentFieldKind kind, ReadOnlyMemory<byte> value)
    {
        if (!StoresBytes(kind))
        {
            throw new ArgumentException(
                FormattableString.Invariant($"A {kind} field does not store bytes."), nameof(kind));
        }

        return new ContentFieldValue(kind, 0, value, false);
    }

    /// <summary>True for the four kinds whose payload is <see cref="Number"/>.</summary>
    public static bool StoresNumber(ContentFieldKind kind) => kind is ContentFieldKind.Int
        or ContentFieldKind.ScaledInt
        or ContentFieldKind.Bool
        or ContentFieldKind.KeyReference;

    /// <summary>True for the two kinds whose payload is <see cref="Bytes"/>.</summary>
    public static bool StoresBytes(ContentFieldKind kind)
        => kind is ContentFieldKind.TagList or ContentFieldKind.OpaqueBytes;

    /// <inheritdoc />
    public bool Equals(ContentFieldValue other)
        => Kind == other.Kind
            && Number == other.Number
            && IsAbsent == other.IsAbsent
            && Bytes.Span.SequenceEqual(other.Bytes.Span);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ContentFieldValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, Number, IsAbsent, Bytes.Length);

    /// <summary>Value equality, with the bytes compared by content rather than by reference.</summary>
    public static bool operator ==(ContentFieldValue left, ContentFieldValue right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(ContentFieldValue left, ContentFieldValue right) => !left.Equals(right);
}

/// <summary>
/// The generic row a codec-free consumer sees (spec 2.2): its id, its key, its parent id, its retired bit
/// and an ordered field-value list PARALLEL to its type's schema.
/// <para>
/// Nothing here is validated. An id of 0, a negative id and a non-zero parent id are all FINDINGS
/// (<c>KEC0009</c> and <c>KEC0031</c>), so the row has to be able to hold the bad value or the validator's
/// sweep would never see it and a bulk import could not report every defect in one pass.
/// </para>
/// </summary>
public sealed class ContentRow
{
    /// <summary>Builds a row. The field values are copied, so the row is immutable once built.</summary>
    public ContentRow(
        ContentTypeId type,
        int id,
        ContentKey key,
        int parentId,
        bool isRetired,
        IReadOnlyList<ContentFieldValue> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        Type = type;
        Id = id;
        Key = key;
        ParentId = parentId;
        IsRetired = isRetired;

        var copy = new ContentFieldValue[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            copy[i] = fields[i];
        }

        Fields = copy;
    }

    /// <summary>The content type this row belongs to.</summary>
    public ContentTypeId Type { get; }

    /// <summary>The definition id, unique and never reused within its type (contracts 5.1).</summary>
    public int Id { get; }

    /// <summary>The string key, unique within its type and immutable once published (contracts 5.3).</summary>
    public ContentKey Key { get; }

    /// <summary>The inheritance parent, 0 throughout phase 1. <c>KEC0031</c> refuses a non-zero value.</summary>
    public int ParentId { get; }

    /// <summary>Whether the row is retired. A retire is irreversible (contracts 8.6).</summary>
    public bool IsRetired { get; }

    /// <summary>The values, parallel BY INDEX to the type's <see cref="ContentFieldSchema.Fields"/>.</summary>
    public IReadOnlyList<ContentFieldValue> Fields { get; }
}
