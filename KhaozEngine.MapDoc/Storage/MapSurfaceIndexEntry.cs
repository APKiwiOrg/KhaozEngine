using System.Collections.Generic;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

public readonly record struct MapSlotRect(long MinX, long MinZ, long MaxXExclusive, long MaxZExclusive)
{
    internal bool Contains(long x, long z) => x >= MinX && x < MaxXExclusive && z >= MinZ && z < MaxZExclusive;
    internal bool Overlaps(MapSlotRect other) => MinX < other.MaxXExclusive && MaxXExclusive > other.MinX &&
        MinZ < other.MaxZExclusive && MaxZExclusive > other.MinZ;
}
public readonly record struct MapCellRect(long MinX, long MinZ, long MaxXExclusive, long MaxZExclusive)
{
    internal bool Overlaps(MapCellRect other) => MinX < other.MaxXExclusive && MaxXExclusive > other.MinX &&
        MinZ < other.MaxZExclusive && MaxZExclusive > other.MinZ;
}
public sealed record MapSurfaceIndexEntry(MapPatchKey Key, MapCellRect Cells, int MinHeightUnits,
    int MaxHeightUnits, string PayloadSha256, string SemanticSha256, IReadOnlyList<MapPatchKey> Dependencies,
    IReadOnlyList<string> RecordIds, IReadOnlyList<MapRecordRef> IncidentRecords, IReadOnlyList<string> SpaceIds,
    [property: JsonIgnore] bool Loaded);
public sealed record MapIndexPageRef(MapSlotRect Covers, string Sha256, int EntryCount);
public sealed record MapDirectoryPageRef(string SurfaceId, MapSlotRect Covers, string Sha256);
internal sealed record MapSurfacePacking(int IndexBlockSlots = 16, int DirectoryBlockPages = 16);
