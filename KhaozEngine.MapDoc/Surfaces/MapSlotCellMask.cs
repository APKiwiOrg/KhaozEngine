using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>An immutable set of slot-relative cells, independent of a patch's authored rectangle.</summary>
internal sealed class MapSlotCellMask
{
    readonly ulong[] _words;
    internal static MapSlotCellMask All { get; } = CreateAll();
    internal int Count { get; }

    MapSlotCellMask(ulong[] words)
    {
        _words = words;
        foreach (ulong word in words) Count += BitOperations.PopCount(word);
    }

    static MapSlotCellMask CreateAll()
    {
        var words = new ulong[64];
        Array.Fill(words, ulong.MaxValue);
        return new(words);
    }

    internal static MapSlotCellMask Of(IEnumerable<int> slotCells)
    {
        ArgumentNullException.ThrowIfNull(slotCells);
        var words = new ulong[64];
        foreach (int cell in slotCells)
        {
            RequireCell(cell);
            words[cell / 64] |= 1UL << (cell % 64);
        }
        return new(words);
    }

    internal MapSlotCellMask Union(MapSlotCellMask other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var words = new ulong[64];
        for (int i = 0; i < words.Length; i++) words[i] = _words[i] | other._words[i];
        return new(words);
    }

    internal bool Contains(int slotCell)
    {
        RequireCell(slotCell);
        return (_words[slotCell / 64] & (1UL << (slotCell % 64))) != 0;
    }

    static void RequireCell(int cell)
    {
        if (cell is < 0 or >= 4096) throw new ArgumentOutOfRangeException(nameof(cell));
    }
}
