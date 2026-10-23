using System;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Calling-thread observations for synchronous storage operations. Counters never admit or limit work.</summary>
internal sealed class MapFoliageStorageWork : IDisposable
{
    [ThreadStatic] static MapFoliageStorageWork? t_current;
    readonly MapFoliageStorageWork? _previous;
    internal static MapFoliageStorageWork? Current => t_current;
    MapFoliageStorageWork() { _previous = t_current; t_current = this; }
    internal static MapFoliageStorageWork Capture() => new();
    internal int PackReads;
    internal int PackWrites;
    internal int PackEncodes;
    internal long PackPathFormats;
    internal long ChunkInspections;
    internal int ChunkHashes;
    internal int InlineWrites;
    internal int DocumentGates;
    internal static MapFoliageChunkRef Inspect(MapFoliageChunkRef row)
    {
        if (t_current is { } work) work.ChunkInspections++;
        return row;
    }
    public void Dispose() => t_current = _previous;
}
