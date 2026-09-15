using System;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// The typed view over the <c>item</c> type of spec section 9.1. A <c>ref struct</c> over the body span
/// with the four hot fields decoded at construction, because the stacking rule reads <c>stackable</c> and
/// <c>max_stack</c> on every merge test and nothing on that path can afford a field-by-name walk.
/// <para>
/// Section 9.1 says "known offsets". In a positional varint row the offsets are not constant, so this
/// walks the preceding fields with length skips instead: one pass, no branch on a name, no allocation.
/// </para>
/// </summary>
public readonly ref struct ItemRowView
{
    private ItemRowView(bool stackable, int maxStack, int durabilityMax, int socketMax, bool retired, bool valid)
    {
        Stackable = stackable;
        MaxStack = maxStack;
        DurabilityMax = durabilityMax;
        SocketMax = socketMax;
        IsRetired = retired;
        IsValid = valid;
    }

    public bool Stackable { get; }

    public int MaxStack { get; }

    public int DurabilityMax { get; }

    public int SocketMax { get; }

    public bool IsRetired { get; }

    public bool IsValid { get; }

    public static ItemRowView Decode(ReadOnlySpan<byte> body, bool retired)
    {
        int offset = 0;
        if (!SkipLengthPrefixed(body, ref offset)) return default;          // content key
        if (!ContentVarint.TryRead(body, ref offset, out uint tagCount)) return default;
        for (uint i = 0; i < tagCount; i++)
        {
            if (!ContentVarint.TryRead(body, ref offset, out _)) return default;
        }
        if (offset >= body.Length) return default;
        bool stackable = body[offset++] != 0;
        if (!ContentVarint.TryRead(body, ref offset, out uint maxStack)) return default;
        if (offset >= body.Length) return default;
        offset++;                                                          // tradable
        if (!ContentVarint.TryRead(body, ref offset, out _)) return default;   // value
        if (!SkipLengthPrefixed(body, ref offset)) return default;             // icon
        if (!SkipLengthPrefixed(body, ref offset)) return default;             // mesh
        if (!SkipLengthPrefixed(body, ref offset)) return default;             // held_mesh
        if (!ContentVarint.TryRead(body, ref offset, out _)) return default;   // ground_pose
        if (!ContentVarint.TryRead(body, ref offset, out _)) return default;   // icon_tilt
        if (!ContentVarint.TryRead(body, ref offset, out _)) return default;   // icon_spin
        if (!ContentVarint.TryRead(body, ref offset, out uint durability)) return default;
        if (!ContentVarint.TryRead(body, ref offset, out uint sockets)) return default;
        return new ItemRowView(stackable, (int)maxStack, (int)durability, (int)sockets, retired, true);
    }

    private static bool SkipLengthPrefixed(ReadOnlySpan<byte> body, ref int offset)
    {
        if (!ContentVarint.TryRead(body, ref offset, out uint length)) return false;
        if (offset + (int)length > body.Length) return false;
        offset += (int)length;
        return true;
    }
}
