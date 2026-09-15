using System;

namespace KhaozEngine.Catalog;

/// <summary>
/// The loaded shape of ONE content type, spec 9.1: parallel arrays indexed by id, one concatenated
/// <c>Bodies</c> blob holding every row body of every chunk, and a bitset for the retired flag. A lookup is
/// <c>Offsets[id]</c>, one array read, then a <see cref="ReadOnlySpan{T}"/> slice of <c>Bodies</c>. No
/// dictionary, no lock, no allocation.
/// <para>
/// <b><see cref="Offsets"/> is sized to the highest id this version carries a row under, plus one.</b> It is
/// NOT sized to the sum of the type's chunk slots: a chunk slot is a TRANSPORT unit (contracts 4.5) and the
/// runtime does not inherit its sparseness, so a type with ids 1 to 35 and a chunk size of 1,024 allocates a
/// 36 element array.
/// </para>
/// <para>
/// <b>There is no <c>Keys</c> array, because every row body already opens with its own key</b>, length
/// prefixed UTF-8 (spec 7.3). The per-type key blob IS <see cref="Bodies"/>, so an id-to-key read is the same
/// array read and span slice as a row read, one varint further in. What a key costs is its bucket in
/// <see cref="KeyIds"/> and nothing else.
/// </para>
/// <para>
/// Internal because it is the runtime's storage rather than its seam: a consumer reads through
/// <see cref="ContentRuntime"/>, which is what implements <see cref="IContentSnapshot"/>. Every field here is
/// readonly and no array is written after construction, which is what makes the swap of spec 9.7 need no
/// lock and no barrier beyond the publishing write.
/// </para>
/// </summary>
internal sealed class ContentTypeTable
{
    /// <summary>
    /// Builds one type's table from rows ALREADY ordered by id, with their encoded bodies in one blob.
    /// <para>
    /// The blob is taken by reference rather than copied, because the caller's blob is already the
    /// concatenation spec 9.2 asks for: every body, in ascending id order, in one allocation. Copying it
    /// again would hold the catalog twice at exactly the boot peak where that is least affordable.
    /// </para>
    /// </summary>
    /// <param name="type">The content type this table holds.</param>
    /// <param name="rows">The rows, ascending by id. Ids below 1 are held but not indexed.</param>
    /// <param name="bodies">The concatenated row bodies, or an empty array when no row arrived with one.</param>
    /// <param name="bodyOffsets">Each row's offset into <paramref name="bodies"/>, or -1 for no body.</param>
    /// <param name="bodyLengths">Each row's body length, parallel to <paramref name="bodyOffsets"/>.</param>
    internal ContentTypeTable(
        ContentTypeId type,
        ContentRow[] rows,
        byte[] bodies,
        int[] bodyOffsets,
        int[] bodyLengths)
    {
        Type = type;
        Rows = rows;
        Bodies = bodies;

        // The highest id the version carries a row under, retired or not: a retired row keeps its slot and
        // its bytes (spec 3.9), so it sizes the table exactly as a live one does. An id below 1 is KEC0009's
        // finding rather than this table's refusal, and it takes no slot because there is none to take.
        int highest = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i].Id > highest)
            {
                highest = rows[i].Id;
            }
        }

        int slots = highest + 1;
        Offsets = new int[slots];
        Lengths = new int[slots];
        RowIndex = new int[slots];
        RetiredBits = new ulong[(slots + 63) / 64];
        Offsets.AsSpan().Fill(-1);
        RowIndex.AsSpan().Fill(-1);

        int indexed = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            ContentRow row = rows[i];
            int id = row.Id;
            if (id < 1 || RowIndex[id] >= 0)
            {
                // A duplicate id is KEC0036 rather than a load failure, so the FIRST row in id order answers
                // the lookup and the loser stays in Rows for the validator to report.
                continue;
            }

            RowIndex[id] = i;
            indexed++;
            if (bodyOffsets[i] >= 0)
            {
                Offsets[id] = bodyOffsets[i];
                Lengths[id] = bodyLengths[i];
            }

            if (row.IsRetired)
            {
                RetiredBits[id >> 6] |= 1UL << (id & 63);
            }
        }

        RowCount = indexed;
        KeyIds = BuildKeyIndex(indexed);
    }

    /// <summary>The content type this table holds.</summary>
    internal ContentTypeId Type { get; }

    /// <summary>
    /// Every row of this type, ASCENDING by id, which is what the generic read side of
    /// <see cref="IContentSnapshot"/> hands back.
    /// <para>
    /// Spec 9.1's table does not list it, because that section is describing the hot path, where a lookup
    /// answers with a span over <see cref="Bodies"/> and decodes nothing. The seam the runtime implements
    /// asks for a <see cref="ContentRow"/> by id and for every row of a type, and a server decodes EAGERLY
    /// at boot (spec 9.3), so the decoded rows are RETAINED here rather than re-decoded per call. Retaining
    /// them costs nothing at the boot peak, since the rows exist already, and it is what keeps the generic
    /// path allocation free too.
    /// </para>
    /// </summary>
    internal ContentRow[] Rows { get; }

    /// <summary>Index by id: the row's offset into <see cref="Bodies"/>, or -1 when it carries no body.</summary>
    internal int[] Offsets { get; }

    /// <summary>Index by id: the row body's length, parallel to <see cref="Offsets"/>.</summary>
    internal int[] Lengths { get; }

    /// <summary>
    /// Index by id: the row's index into <see cref="Rows"/>, or -1 when this version carries no row under
    /// that id. This is what answers PRESENCE, because <see cref="Offsets"/> answers whether a row arrived
    /// with its encoded BODY, and a snapshot built row by row (a publish candidate, a test) carries rows
    /// with no bodies at all.
    /// </summary>
    internal int[] RowIndex { get; }

    /// <summary>Every row body of every chunk, concatenated in ascending id order, one allocation.</summary>
    internal byte[] Bodies { get; }

    /// <summary>One bit per slot, parallel to <see cref="Offsets"/>, so the retired flag costs no decode.</summary>
    internal ulong[] RetiredBits { get; }

    /// <summary>
    /// The open-addressed key-to-id index of spec 9.4: a power-of-two <c>int[]</c> of at least twice the row
    /// count, holding an id in an occupied bucket and <c>0</c> in an empty one, which is unambiguous because
    /// 0 is not a legal definition id (contracts 5.1). A probe hashes the key's UTF-8 bytes, masks to a
    /// bucket, compares the candidate id's key SLICE ordinally, and walks linearly on a collision.
    /// </summary>
    internal int[] KeyIds { get; }

    /// <summary>How many distinct ids this table answers for, which is what sized <see cref="KeyIds"/>.</summary>
    internal int RowCount { get; }

    /// <summary>The id space the table covers, which is the highest id it holds a row under plus one.</summary>
    internal int SlotCount => Offsets.Length;

    /// <summary>True when this version carries a row under that id, retired or not.</summary>
    internal bool HasRow(int id) => (uint)id < (uint)RowIndex.Length && RowIndex[id] >= 0;

    /// <summary>The retired bit of spec 3.9, read from the bitset without touching the row.</summary>
    internal bool IsRetired(int id)
        => HasRow(id) && (RetiredBits[id >> 6] & (1UL << (id & 63))) != 0;

    /// <summary>One row by id, or null when this version carries none under it.</summary>
    internal ContentRow? Row(int id) => HasRow(id) ? Rows[RowIndex[id]] : null;

    /// <summary>
    /// The row's encoded body: one array read and one span slice, which is the whole lookup path spec 9.1
    /// describes and the one budget P7 is taken against. Empty for an id the version does not carry and for
    /// a row that arrived without its bytes.
    /// </summary>
    internal ReadOnlySpan<byte> Body(int id)
    {
        if ((uint)id >= (uint)Offsets.Length)
        {
            return default;
        }

        int offset = Offsets[id];
        return offset < 0 ? default : Bodies.AsSpan(offset, Lengths[id]);
    }

    /// <summary>
    /// The row's key as a <see cref="ContentKey"/> over the bytes the table already holds, with no copy and
    /// no string.
    /// <para>
    /// Out of <see cref="Bodies"/> where the encoder put it, one varint in, for a row that arrived with its
    /// body. A row that did not, which is what a snapshot built row by row carries, answers with the key its
    /// <see cref="ContentRow"/> holds instead, so both construction paths compare the same ordinal bytes.
    /// </para>
    /// </summary>
    internal ContentKey Key(int id)
    {
        if (!HasRow(id))
        {
            return default;
        }

        int offset = Offsets[id];
        if (offset < 0)
        {
            return Rows[RowIndex[id]].Key;
        }

        int cursor = 0;
        ReadOnlySpan<byte> body = Bodies.AsSpan(offset, Lengths[id]);
        if (!ContentVarint.TryRead(body, ref cursor, out uint length, out _) || length > (uint)(body.Length - cursor))
        {
            // A body whose key prefix does not decode is a malformed row, which is the validator's finding
            // and not a load failure. The row's own key still answers, so a lookup does not silently vanish.
            return Rows[RowIndex[id]].Key;
        }

        return new ContentKey(Bodies, offset + cursor, (int)length);
    }

    /// <summary>
    /// One id by key, through the open-addressed index. ORDINAL over the UTF-8 bytes (contracts 5.3), and no
    /// string is materialised on either side.
    /// </summary>
    internal bool TryGetId(ReadOnlySpan<byte> key, out int id)
    {
        int[] buckets = KeyIds;
        if (buckets.Length == 0 || key.Length == 0)
        {
            id = 0;
            return false;
        }

        int mask = buckets.Length - 1;
        int slot = (int)(ContentKey.HashBytes(key) & (uint)mask);
        while (true)
        {
            int candidate = buckets[slot];
            if (candidate == 0)
            {
                id = 0;
                return false;
            }

            if (Key(candidate).Utf8.SequenceEqual(key))
            {
                id = candidate;
                return true;
            }

            slot = (slot + 1) & mask;
        }
    }

    /// <summary>
    /// The managed bytes this table's own arrays hold, which is the memory line of spec 9.2. It does not
    /// count the decoded <see cref="Rows"/>, which the loader allocated before the table existed.
    /// </summary>
    internal long ApproximateBytes()
        => Bodies.LongLength
            + ((long)Offsets.Length * 4)
            + ((long)Lengths.Length * 4)
            + ((long)RowIndex.Length * 4)
            + ((long)RetiredBits.Length * 8)
            + ((long)KeyIds.Length * 4);

    /// <summary>
    /// Builds the key index in ONE linear pass over the indexed ids, which is the build the sorted-key
    /// alternative lost on (spec 9.1). A duplicate key is KEC0002's finding rather than a load failure, so
    /// the first id in id order keeps the bucket.
    /// </summary>
    int[] BuildKeyIndex(int rowCount)
    {
        if (rowCount == 0)
        {
            return [];
        }

        int capacity = 2;
        while (capacity < rowCount * 2)
        {
            capacity <<= 1;
        }

        int[] buckets = new int[capacity];
        int mask = capacity - 1;
        for (int id = 1; id < RowIndex.Length; id++)
        {
            if (RowIndex[id] < 0)
            {
                continue;
            }

            ReadOnlySpan<byte> key = Key(id).Utf8;
            if (key.Length == 0)
            {
                // An empty key is KEC0001 and cannot be probed for, since a probe with no bytes is refused.
                continue;
            }

            int slot = (int)(ContentKey.HashBytes(key) & (uint)mask);
            while (buckets[slot] != 0)
            {
                if (Key(buckets[slot]).Utf8.SequenceEqual(key))
                {
                    slot = -1;
                    break;
                }

                slot = (slot + 1) & mask;
            }

            if (slot >= 0)
            {
                buckets[slot] = id;
            }
        }

        return buckets;
    }
}
