using System;

namespace KhaozEngine.Primitives;

/// <summary>A conservative code span per 8 by 8 cells, built off-thread with the immutable field.</summary>
internal sealed class InteriorOverlapSummary
{
    const int BlockCells = 8;
    readonly int _blocks;
    readonly ushort[] _spans;

    internal InteriorOverlapSummary(int cells, ReadOnlySpan<byte> texels)
    {
        _blocks = (cells + BlockCells - 1) / BlockCells;
        _spans = new ushort[_blocks * _blocks * 2];
        for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                int cell = (z * cells + x) * 4;
                int lo = (texels[cell] << 8) | texels[cell + 1];
                int hi = (texels[cell + 2] << 8) | texels[cell + 3];
                if (hi <= lo) continue;
                int block = ((z / BlockCells) * _blocks + x / BlockCells) * 2;
                _spans[block] = _spans[block + 1] == 0 ? (ushort)lo : Math.Min(_spans[block], (ushort)lo);
                _spans[block + 1] = Math.Max(_spans[block + 1], (ushort)hi);
            }
    }

    internal bool MayIntersect(int firstX, int lastX, int firstZ, int lastZ, float low, float high)
    {
        for (int z = firstZ / BlockCells; z <= lastZ / BlockCells; z++)
            for (int x = firstX / BlockCells; x <= lastX / BlockCells; x++)
            {
                int at = (z * _blocks + x) * 2;
                int lo = _spans[at], hi = _spans[at + 1];
                if (hi > lo && high >= lo && low < hi) return true;
            }
        return false;
    }
}
