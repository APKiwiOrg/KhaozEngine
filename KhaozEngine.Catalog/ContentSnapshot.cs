using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog;

/// <summary>
/// A complete candidate, or a version loaded but not yet active: every type's rows plus the version header
/// (spec 2.2). It is the concrete holder behind <see cref="IContentSnapshot"/> that a publish validates, a
/// boot decodes into and a test builds by hand.
/// <para>
/// Immutable once built, and built ONE way, through <see cref="ContentSnapshotBuilder"/>, so publish, boot
/// and a test all produce the same shape. Rows come back ordered by id whatever order they went in, which is
/// what makes two snapshots of the same content compare row for row.
/// </para>
/// <para>
/// <b>Nothing here refuses bad content.</b> A duplicate id, a duplicate key, an id of 0 and a malformed key
/// are all FINDINGS (<c>KEC0002</c>, <c>KEC0009</c>, <c>KEC0036</c>), so a snapshot that could not hold the
/// defect would be a snapshot the validator could never report it from. Where two rows share an id or a key,
/// the FIRST in id order answers the lookup and both stay in <see cref="Rows"/>.
/// </para>
/// </summary>
public sealed class ContentSnapshot : IContentSnapshot
{
    static readonly ContentRow[] NoRows = [];

    readonly Dictionary<ContentTypeId, ContentSnapshotTable> _tables;
    readonly ContentTypeId[] _types;
    readonly RemapRule[] _rules;
    readonly ContentSnapshotTable? _items;

    /// <summary>Takes the finished tables from the builder, which is the only caller.</summary>
    internal ContentSnapshot(ContentVersionIdentity identity, RemapRule[] rules, ContentSnapshotTable[] tables)
    {
        Identity = identity;
        _rules = rules;
        _tables = new Dictionary<ContentTypeId, ContentSnapshotTable>(tables.Length);
        _types = new ContentTypeId[tables.Length];
        for (int i = 0; i < tables.Length; i++)
        {
            ContentSnapshotTable table = tables[i];
            _tables.Add(table.Type, table);
            _types[i] = table.Type;
        }

        // The typed item view is the one hot lookup that skips the dictionary, because Scope B reads it per
        // operation and the item type id is the engine's own fixed 2.
        _tables.TryGetValue(new ContentTypeId(EngineContentTypes.ItemTypeId), out _items);
    }

    /// <inheritdoc />
    public int VersionNumber => Identity.Number;

    /// <inheritdoc />
    public ContentVersionIdentity Identity { get; }

    /// <inheritdoc />
    public IReadOnlyList<RemapRule> Rules => _rules;

    /// <summary>Every content type this snapshot carries a row for, ASCENDING by type id.</summary>
    public IReadOnlyList<ContentTypeId> Types => _types;

    /// <inheritdoc />
    public bool TryGetRow(ContentTypeId type, int id, [MaybeNullWhen(false)] out ContentRow row)
    {
        if (_tables.TryGetValue(type, out ContentSnapshotTable? table))
        {
            return table.TryGetRow(id, out row);
        }

        row = null;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetId(ContentTypeId type, ContentKey key, out int id)
    {
        if (_tables.TryGetValue(type, out ContentSnapshotTable? table))
        {
            return table.TryGetId(key, out id);
        }

        id = 0;
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<ContentRow> Rows(ContentTypeId type)
        => _tables.TryGetValue(type, out ContentSnapshotTable? table) ? table.Rows : NoRows;

    /// <inheritdoc />
    public bool IsRetired(ContentTypeId type, int id)
        => _tables.TryGetValue(type, out ContentSnapshotTable? table) && table.IsRetired(id);

    /// <summary>
    /// The typed <c>item</c> view of spec 9.1: one array read, one span slice and four varint reads, with no
    /// allocation and no walk by field name.
    /// <para>
    /// It reads the ENCODED BODY, so it answers only for a snapshot whose rows arrived with their bodies,
    /// which a pack load is. A snapshot built from rows alone, which a publish candidate and a validator test
    /// are, answers false and is read through <see cref="TryGetRow"/> instead.
    /// </para>
    /// </summary>
    public bool TryGetItem(int id, out ItemRow row)
    {
        ContentSnapshotTable? items = _items;
        if (items is null || !items.TryGetBody(id, out byte[]? bodies, out int start, out int length, out bool retired))
        {
            row = default;
            return false;
        }

        return ItemRow.TryDecode(bodies, start, length, retired, out row);
    }
}

/// <summary>
/// One content type's rows inside a snapshot, in the shape spec 9.1 gives the loaded runtime: the rows in id
/// order, an id index, a key index, a retired BITSET so the flag is read without touching a row, and the
/// encoded bodies concatenated into one blob so a key is a slice of bytes the snapshot already holds.
/// </summary>
internal sealed class ContentSnapshotTable
{
    readonly ContentRow[] _rows;
    readonly Dictionary<int, int> _byId;
    readonly Dictionary<ContentKey, int> _byKey;
    readonly ulong[] _retired;
    readonly byte[]? _bodies;
    readonly int[] _bodyOffsets;
    readonly int[] _bodyLengths;

    /// <summary>Indexes rows that are ALREADY in id order, with their bodies parallel to them.</summary>
    /// <param name="type">The content type these rows belong to.</param>
    /// <param name="rows">The rows, ascending by id, ties in the order they were added.</param>
    /// <param name="bodies">Each row's encoded body, or an empty one where the row arrived without it.</param>
    internal ContentSnapshotTable(ContentTypeId type, ContentRow[] rows, ReadOnlyMemory<byte>[] bodies)
    {
        Type = type;
        _rows = rows;
        _byId = new Dictionary<int, int>(rows.Length);
        _byKey = new Dictionary<ContentKey, int>(rows.Length);
        _retired = new ulong[(rows.Length + 63) / 64];
        _bodyOffsets = new int[rows.Length];
        _bodyLengths = new int[rows.Length];

        int total = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            // A duplicate id or key is a finding rather than a refusal, so the FIRST row in id order wins the
            // index and the second is still in Rows for the validator to see.
            _byId.TryAdd(rows[i].Id, i);
            _byKey.TryAdd(rows[i].Key, i);
            if (rows[i].IsRetired)
            {
                _retired[i >> 6] |= 1UL << (i & 63);
            }

            total += bodies[i].Length;
        }

        if (total == 0)
        {
            _bodyOffsets.AsSpan().Fill(-1);
            return;
        }

        _bodies = new byte[total];
        int written = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            ReadOnlySpan<byte> body = bodies[i].Span;
            if (body.Length == 0)
            {
                _bodyOffsets[i] = -1;
                continue;
            }

            body.CopyTo(_bodies.AsSpan(written));
            _bodyOffsets[i] = written;
            _bodyLengths[i] = body.Length;
            written += body.Length;
        }
    }

    /// <summary>The content type this table holds.</summary>
    internal ContentTypeId Type { get; }

    /// <summary>The rows, ascending by id.</summary>
    internal IReadOnlyList<ContentRow> Rows => _rows;

    /// <summary>One row by id.</summary>
    internal bool TryGetRow(int id, [MaybeNullWhen(false)] out ContentRow row)
    {
        if (_byId.TryGetValue(id, out int index))
        {
            row = _rows[index];
            return true;
        }

        row = null;
        return false;
    }

    /// <summary>One id by key, ordinal over the UTF-8 bytes.</summary>
    internal bool TryGetId(ContentKey key, out int id)
    {
        if (_byKey.TryGetValue(key, out int index))
        {
            id = _rows[index].Id;
            return true;
        }

        id = 0;
        return false;
    }

    /// <summary>The retired bit, read from the bitset rather than from the row.</summary>
    internal bool IsRetired(int id)
        => _byId.TryGetValue(id, out int index) && (_retired[index >> 6] & (1UL << (index & 63))) != 0;

    /// <summary>One row's encoded body as a range of the concatenated blob, with no copy.</summary>
    internal bool TryGetBody(
        int id,
        [MaybeNullWhen(false)] out byte[] bodies,
        out int start,
        out int length,
        out bool isRetired)
    {
        if (_bodies is not null && _byId.TryGetValue(id, out int index) && _bodyOffsets[index] >= 0)
        {
            bodies = _bodies;
            start = _bodyOffsets[index];
            length = _bodyLengths[index];
            isRetired = (_retired[index >> 6] & (1UL << (index & 63))) != 0;
            return true;
        }

        bodies = null;
        start = 0;
        length = 0;
        isRetired = false;
        return false;
    }
}
