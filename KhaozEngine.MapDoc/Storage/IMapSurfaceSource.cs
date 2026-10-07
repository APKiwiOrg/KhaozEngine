using System.Collections.Generic;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

public enum MapPatchStatus { Present, KnownEmpty, Unloaded, Missing, Corrupt }
public sealed record MapPatchRead(MapPatchKey Key, MapPatchStatus Status, MapSurfacePatch? Patch,
    string? SemanticSha256, string? Detail, int PagesRead);
public sealed record MapQueryLimits(int MaxCandidatePatches = 256, int MaxInspectedFaces = 4096,
    int MaxSupportIntersections = 64, int MaxPageReads = 32, int MaxRecordReads = 64, int MaxRecordDepth = 8);
public sealed record MapCoveredRange(string SurfaceId, MapSlotRect Slots);
public enum MapFindStatus { Complete, Incomplete, CapacityExceeded }
public sealed record MapPatchFindResult(MapFindStatus Status, MapSurfaceScope Scope, string SnapshotId,
    IReadOnlyList<MapPatchRead> Patches, IReadOnlyList<MapCoveredRange> KnownEmpty,
    IReadOnlyList<MapPatchRead> Unavailable, int PagesRead);

/// <summary>A trusted producer of generation-pinned, defensively copied surface payloads.</summary>
public interface IMapSurfaceSource
{
    string SnapshotId { get; }
    string RootSha256 { get; }
    IReadOnlyList<MapSurfaceRef> Surfaces { get; }
    MapPatchRead ReadPatch(MapPatchKey key);
    MapPatchFindResult FindPatches(MapSurfaceScope scope);
}
