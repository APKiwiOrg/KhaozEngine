using System;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// The loaded shape of one content type, spec section 9.1: parallel arrays indexed by id, one concatenated
/// <c>Bodies</c> buffer for every row of every chunk, and a bitset for the retired flag. A lookup is one
/// array read and a span slice, with no dictionary, no lock and no allocation.
/// </summary>
public sealed class ContentTypeTable
{
    public ContentTypeTable(ushort typeId, string typeKey, int chunkSlots, int highestId, int bodyBytes)
    {
        TypeId = typeId;
        TypeKey = typeKey;
        ChunkSlots = chunkSlots;
        int slots = highestId + 1;
        Offsets = new int[slots];
        Lengths = new int[slots];
        Keys = new string[slots];
        RetiredBits = new ulong[(slots + 63) / 64];
        Bodies = new byte[bodyBytes];
        Array.Fill(Offsets, -1);
    }

    public ushort TypeId { get; }

    public string TypeKey { get; }

    public int ChunkSlots { get; }

    public int[] Offsets { get; }

    public int[] Lengths { get; }

    public string[] Keys { get; }

    public ulong[] RetiredBits { get; }

    public byte[] Bodies { get; }

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

    /// <summary>Approximate managed bytes this table holds, for the memory line of budget P3.</summary>
    public long ApproximateBytes()
    {
        long bytes = Bodies.LongLength
            + ((long)Offsets.Length * 4)
            + ((long)Lengths.Length * 4)
            + ((long)Keys.Length * 8)
            + ((long)RetiredBits.Length * 8);
        foreach (string? key in Keys)
        {
            if (key is not null) bytes += 24 + (key.Length * 2);
        }
        return bytes;
    }
}
