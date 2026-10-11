using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>How a body's level meets the space above it. <see cref="FreeSurface"/> lies strictly below the space's
/// upper bound or under an open top, <see cref="Closed"/> meets or lies above the upper bound, and
/// <see cref="Unresolved"/> is area whose bounds the compile could not read or prove.</summary>
public enum MapWaterRegionKind : byte { FreeSurface, Closed, Unresolved }

/// <summary>One kind of area of a body within one covered space. Every triangle lies at
/// <c>Y = FromSingle(SurfaceY)</c> and faces up, meaning <c>cross(B - A, C - A).Y &gt; 0</c>. Triangles are closed
/// drawing geometry and may share boundaries.</summary>
public sealed record MapWaterRegion(string SpaceId, MapWaterRegionKind Kind, IReadOnlyList<MapExactTriangle> Triangles);

public enum MapWaterCompileStatus { Complete, Incomplete, CapacityExceeded, NotRepresentable, Invalid }

/// <summary>The counted work of one body's compile. <see cref="UpperFaces"/> and <see cref="LowerFaces"/> are the bound
/// faces read, <see cref="Triangles"/> the region triangles emitted.</summary>
public sealed record MapWaterWork(int Pieces, int PieceVertices, int FootprintRects, int FootprintCells, int BoundReads,
    int UpperFaces, int LowerFaces, int PairChecks, int Triangles);

/// <summary>The work one body's compile may spend. A null <see cref="Reads"/> means the released
/// <see cref="MapRefinementLimits"/> defaults, under which every bound read is chunked.</summary>
public sealed record MapWaterCompileLimits(int MaxPieces = MapWaterDomain.MaxPieces,
    int MaxPieceVertices = MapWaterDomain.MaxPieceVertices, int MaxFootprintCells = 4_194_304, int MaxBoundFaces = 1_048_576,
    int MaxPairChecks = (int)MapWaterDomain.MaxPairChecks, int MaxTriangles = 1_048_576, MapRefinementLimits? Reads = null);

/// <summary>One water body compiled against an acquisition: its regions split exactly at every ceiling contour, the
/// deepest bed under it, its status and counted work, and a digest that covers exactly what it read.
/// <see cref="DeepestBedY"/> and <see cref="Bounds"/> are null when no lower area resolved. A refused body keeps no
/// regions.</summary>
public sealed record MapCompiledWaterBody(string BodyId, float SurfaceY, MapWaterMediumDoc Medium, string RenderMaterial,
    IReadOnlyList<MapWaterRegion> Regions, float? DeepestBedY, MapBox3? Bounds, MapWaterCompileStatus Status, string? Detail,
    MapWaterWork Work, string Digest)
{
    /// <summary>The compiled domain, or null when it did not compile.</summary>
    internal MapWaterDomain? Domain { get; init; }

    /// <summary>The merged footprint rectangles of each covered space the compile gathered.</summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<MapWaterFootprintRect>> Footprints { get; init; } =
        new Dictionary<string, IReadOnlyList<MapWaterFootprintRect>>(StringComparer.Ordinal);

    /// <summary>The kind of <paramref name="point"/> in the covered space <paramref name="spaceId"/>, or null when the
    /// body's domain does not hold it under OA7 or that space's footprint does not own it, the footprint owning a point
    /// exactly as <see cref="MapSpaceGeometry"/> assigns it to a lattice cell. Within the space a point any closed
    /// triangle holds, boundaries included, is <see cref="MapWaterRegionKind.Closed"/>, so a contour tie closes as
    /// <see cref="MapWaterQuery"/> closes it. Otherwise a point a free triangle holds is
    /// <see cref="MapWaterRegionKind.FreeSurface"/>, and any other owned point is
    /// <see cref="MapWaterRegionKind.Unresolved"/>. Stacked spaces at one XZ classify independently.</summary>
    public MapWaterRegionKind? Classify(string spaceId, MapExactXz point)
    {
        ArgumentNullException.ThrowIfNull(spaceId);
        if (Domain is null || !Domain.Contains(point)) return null;
        if (!Footprints.TryGetValue(spaceId, out IReadOnlyList<MapWaterFootprintRect>? rects) || !rects.Any(r => Owns(r, point))) return null;
        MapWideXz p = MapWideXz.From(point);
        if (Holds(MapWaterRegionKind.Closed)) return MapWaterRegionKind.Closed;
        return Holds(MapWaterRegionKind.FreeSurface) ? MapWaterRegionKind.FreeSurface : MapWaterRegionKind.Unresolved;

        bool Holds(MapWaterRegionKind kind) => Regions.Where(r => r.SpaceId == spaceId && r.Kind == kind)
            .SelectMany(r => r.Triangles).Any(t => MapWaterRegionGeometry.TriangleHolds(t, p));
    }

    static bool Owns(MapWaterFootprintRect rect, MapExactXz point)
    {
        var (x, z) = MapSpaceGeometry.Cell(rect.Frame, point);
        return x >= rect.CellMinX && x < rect.CellMaxX && z >= rect.CellMinZ && z < rect.CellMaxZ;
    }
}
