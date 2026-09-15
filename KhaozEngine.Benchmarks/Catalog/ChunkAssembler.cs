using System;
using System.Collections.Generic;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// One chunk's rows on their way to the encoder. Bodies land in one growable arena and the row list is
/// built from offsets at the end, so a chunk of a thousand rows is one buffer rather than a thousand.
/// </summary>
public sealed class ChunkAssembler
{
    private readonly List<(int Id, bool Retired, int Offset, int Length)> _rows = new(1024);
    private byte[] _arena = new byte[64 * 1024];
    private int _used;

    public int RowCount => _rows.Count;

    public void Reset()
    {
        _rows.Clear();
        _used = 0;
    }

    public Span<byte> Reserve(int maximumBytes)
    {
        while (_used + maximumBytes > _arena.Length) Array.Resize(ref _arena, _arena.Length * 2);
        return _arena.AsSpan(_used, maximumBytes);
    }

    public void Commit(int definitionId, bool retired, int length)
    {
        _rows.Add((definitionId, retired, _used, length));
        _used += length;
    }

    public List<ChunkRowInput> Build()
    {
        var inputs = new List<ChunkRowInput>(_rows.Count);
        foreach ((int id, bool retired, int offset, int length) in _rows)
        {
            inputs.Add(new ChunkRowInput(id, retired, _arena.AsMemory(offset, length)));
        }
        return inputs;
    }

    /// <summary>The largest encoded row in this chunk, which is what <c>KEC0026</c> is checked against.</summary>
    public int LargestRowBytes()
    {
        int largest = 0;
        foreach ((int _, bool _, int _, int length) in _rows)
        {
            if (length > largest) largest = length;
        }
        return largest;
    }
}
