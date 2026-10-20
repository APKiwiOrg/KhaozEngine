using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>True when publication replaces the complete bounded water set, including a null legacy section.</summary>
    public bool Water { get; init; }

    /// <summary>Replaces the foliage root, including an absent section.</summary>
    public bool Foliage { get; init; }

    public bool Prefabs { get; init; }
    public bool BuildingIntent { get; init; }
    public bool NativeResourceTransforms { get; init; }
    public bool NumericReservations { get; init; }
    /// <summary>Replaces both legacy spawn lists together. Native admission permits only explicit adoption or its undo.</summary>
    public bool LegacySpawns { get; init; }

    public static MapNativeWriteSet PlacementsOnly { get; } = new(Array.Empty<MapPatchKey>(),
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<MapCornerOwnerChange>(),
        Array.Empty<ushort>(), true);

    /// <summary>Both write sets' named patches, surfaces, records, spaces, owner changes and materials, and every root
    /// either names.</summary>
    internal static MapNativeWriteSet Union(MapNativeWriteSet a, MapNativeWriteSet b) => new(
        a.Patches.Union(b.Patches).Order().ToArray(), a.SurfaceIds.Union(b.SurfaceIds).Order(StringComparer.Ordinal).ToArray(),
        a.RecordIds.Union(b.RecordIds).Order(StringComparer.Ordinal).ToArray(), a.SpaceIds.Union(b.SpaceIds).Order(StringComparer.Ordinal).ToArray(),
        a.OwnerChanges.Union(b.OwnerChanges).ToArray(), a.Materials.Union(b.Materials).ToArray(), a.Placements || b.Placements)
    {
        Prefabs = a.Prefabs || b.Prefabs,
        BuildingIntent = a.BuildingIntent || b.BuildingIntent,
        NativeResourceTransforms = a.NativeResourceTransforms || b.NativeResourceTransforms,
        NumericReservations = a.NumericReservations || b.NumericReservations,
        Water = a.Water || b.Water,
        NativeAssets = a.NativeAssets || b.NativeAssets,
        Foliage = a.Foliage || b.Foliage,
    };
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
