using System;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// The loaded shape of one content type, spec section 9.1: parallel arrays indexed by id, one concatenated
/// <c>Bodies</c> buffer for every row of every chunk, and a bitset for the retired flag. A lookup is one
/// array read and a span slice, with no dictionary, no lock and no allocation.
/// <para>
/// There is no <c>Keys</c> array. Every row body opens with its own key, length prefixed UTF-8, so the key
/// blob IS <c>Bodies</c> and the offset array IS <c>Offsets</c>. What a key costs is its bucket in
/// <c>KeyIds</c>, the open-addressed key-to-id index of section 9.4.
/// </para>
/// </summary>
public sealed class ContentTypeTable
{
    private int[] _keyIds = [];

    public ContentTypeTable(ushort typeId, string typeKey, int chunkSlots, int highestId, int bodyBytes)
    {
        TypeId = typeId;
        TypeKey = typeKey;
        ChunkSlots = chunkSlots;
        int slots = highestId + 1;
        Offsets = new int[slots];
        Lengths = new int[slots];
        RetiredBits = new ulong[(slots + 63) / 64];
        Bodies = new byte[bodyBytes];
        Array.Fill(Offsets, -1);
    }

    public ushort TypeId { get; }

    public string TypeKey { get; }

    public int ChunkSlots { get; }

    public int[] Offsets { get; }

    public int[] Lengths { get; }

    public ulong[] RetiredBits { get; }

    public byte[] Bodies { get; }

    /// <summary>The open-addressed key to id table: an id in an occupied bucket, 0 in an empty one.</summary>
    public int[] KeyIds => _keyIds;

    public int RowCount { get; set; }

    public bool IsLive(int id) => (uint)id < (uint)Offsets.Length && Offsets[id] >= 0;

    public bool IsRetired(int id) =>
        (uint)id < (uint)Offsets.Length && (RetiredBits[id >> 6] & (1UL << (id & 63))) != 0;

    public void MarkRetired(int id) => RetiredBits[id >> 6] |= 1UL << (id & 63);

    public ReadOnlySpan<byte> Body(int id)
    {
        int offset = Offsets[id];
        return offset < 0 ? default : Bodies.AsSpan(offset, Lengths[id]);
    }

    /// <summary>The row's key as a slice of <c>Bodies</c>, past the length prefix. No allocation.</summary>
    public ReadOnlySpan<byte> KeyUtf8(int id)
    {
        if ((uint)id >= (uint)Offsets.Length) return default;
        int offset = Offsets[id];
        if (offset < 0) return default;
        int cursor = 0;
        ReadOnlySpan<byte> body = Bodies.AsSpan(offset, Lengths[id]);
        if (!ContentVarint.TryRead(body, ref cursor, out uint length)) return default;
        if (cursor + (int)length > body.Length) return default;
        return body.Slice(cursor, (int)length);
    }

    /// <summary>The same slice as a <see cref="ContentKey"/>, which materialises a string only on demand.</summary>
    public ContentKey Key(int id)
    {
        if ((uint)id >= (uint)Offsets.Length) return default;
        int offset = Offsets[id];
        if (offset < 0) return default;
        int cursor = 0;
        ReadOnlySpan<byte> body = Bodies.AsSpan(offset, Lengths[id]);
        if (!ContentVarint.TryRead(body, ref cursor, out uint length)) return default;
        if (cursor + (int)length > body.Length) return default;
        return new ContentKey(Bodies, offset + cursor, (int)length);
    }

    /// <summary>
    /// Builds the key to id index, one linear pass over the live ids. Idempotent, so a caller that needs the
    /// index can ask for it without caring whether the load pass already built it. Id 0 is not a legal id
    /// (contracts 5.1), which is what makes 0 an unambiguous empty bucket.
    /// </summary>
    public void EnsureKeyIndex()
    {
        if (_keyIds.Length > 0) return;
        int capacity = ContentKeyHash.CapacityFor(RowCount);
        int[] buckets = new int[capacity];
        int mask = capacity - 1;
        for (int id = 1; id < Offsets.Length; id++)
        {
            if (Offsets[id] < 0) continue;
            ReadOnlySpan<byte> key = KeyUtf8(id);
            int slot = (int)(ContentKeyHash.Of(key) & (uint)mask);
            while (buckets[slot] != 0)
            {
                // A duplicate key is a validator finding (KEC0002), not a load failure, so the first id wins
                // and the loser is what the validator sees when it looks its own key back up.
                if (KeyUtf8(buckets[slot]).SequenceEqual(key)) break;
                slot = (slot + 1) & mask;
            }
            if (buckets[slot] == 0) buckets[slot] = id;
        }
        _keyIds = buckets;
    }

    public bool TryGetId(ReadOnlySpan<byte> key, out int id)
    {
        int[] buckets = _keyIds;
        if (buckets.Length == 0 || key.Length == 0)
        {
            id = 0;
            return false;
        }
        int mask = buckets.Length - 1;
        int slot = (int)(ContentKeyHash.Of(key) & (uint)mask);
        while (true)
        {
            int candidate = buckets[slot];
            if (candidate == 0)
            {
                id = 0;
                return false;
            }
            if (KeyUtf8(candidate).SequenceEqual(key))
            {
                id = candidate;
                return true;
            }
            slot = (slot + 1) & mask;
        }
    }

    /// <summary>Approximate managed bytes this table holds, for the memory line of budget P3.</summary>
    public long ApproximateBytes() =>
        Bodies.LongLength
        + ((long)Offsets.Length * 4)
        + ((long)Lengths.Length * 4)
        + ((long)RetiredBits.Length * 8)
        + ((long)_keyIds.Length * 4);
}
