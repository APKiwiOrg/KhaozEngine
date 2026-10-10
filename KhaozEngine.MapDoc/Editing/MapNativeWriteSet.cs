using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Editing;

public sealed record MapCornerOwnerChange(MapPatchKey Dependent, int CornerX, int CornerZ,
    MapVertexOwner? OldOwner, MapVertexOwner NewOwner);
public sealed record MapNativeWriteSet(IReadOnlyList<MapPatchKey> Patches, IReadOnlyList<string> SurfaceIds,
    IReadOnlyList<string> RecordIds, IReadOnlyList<string> SpaceIds, IReadOnlyList<MapCornerOwnerChange> OwnerChanges,
    IReadOnlyList<ushort> Materials, bool Placements)
{
    /// <summary>True when the edit replaces the document's native asset roots. Publication then takes the candidate's
    /// whole root list. Not positional, so the released constructor is unchanged.</summary>
    public bool NativeAssets { get; init; }

    public static MapNativeWriteSet PlacementsOnly { get; } = new(Array.Empty<MapPatchKey>(),
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<MapCornerOwnerChange>(),
        Array.Empty<ushort>(), true);
}

public sealed record MapFinePatchRequest(string SourceSurfaceId, IReadOnlyList<MapCellRect> Regions,
    int Subdivision, string FineSurfaceId, bool AcceptAuthoredDifferences);
public enum MapCellConversionClass : byte { ExactCoplanar, ExactDiagonal, UnsupportedEncoding, NotRepresentable }
public sealed record MapCellConversion(long CellX, long CellZ, MapCellConversionClass Class, string? Reason);
public enum MapDifferenceKind : byte { Geometry, Paint, FallbackRemoved, FlagRemoved, ArithmeticPolicy, Retessellated }
public sealed record MapAuthoredDifference(MapDifferenceKind Kind, long CellX, long CellZ,
    MapFaceKey? OldFace, MapFaceKey? NewFace, string Detail);
public sealed record MapConversionResult(MapSurfaceSet Candidate, IReadOnlyList<MapCellConversion> Cells,
    IReadOnlyList<MapAuthoredDifference> Differences, MapNativeWriteSet WriteSet);
