using System.Collections.Generic;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

public enum MapPatchStatus { Present, KnownEmpty, Unloaded, Missing, Corrupt }
/// <summary>A detached patch result. PagesRead counts newly decoded directory and index pages.</summary>
public sealed record MapPatchRead(MapPatchKey Key, MapPatchStatus Status, MapSurfacePatch? Patch,
    string? SemanticSha256, string? Detail, int PagesRead);
/// <summary>Per-call acquisition limits, independent of page cache residency.</summary>
/// <remarks>
/// MaxPageReads bounds directory and index page visits, including cached and unread pages.
/// Captured-source passes share this ceiling and charge repeated visits again.
/// Rectangle seeds and split additions share a ceiling of 1 + 4 * 256 * MaxPageReads, including the merge.
/// PagesRead on results reports only newly decoded pages, so cached and captured calls can report zero
/// while exhausting their traversal or bookkeeping limit. MaxCandidatePatches still bounds patch output.
/// </remarks>
public sealed record MapQueryLimits(int MaxCandidatePatches = 256, int MaxInspectedFaces = 4096,
    int MaxSupportIntersections = 64, int MaxPageReads = 32, int MaxRecordReads = 64, int MaxRecordDepth = 8);
public sealed record MapCoveredRange(string SurfaceId, MapSlotRect Slots);
public enum MapFindStatus { Complete, Incomplete, CapacityExceeded }
/// <summary>Atomic acquisition. CapacityExceeded has no patches, known-empty ranges or unavailable results.</summary>
/// <remarks>PagesRead counts newly decoded pages, not cached or unread metadata visits.</remarks>
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
