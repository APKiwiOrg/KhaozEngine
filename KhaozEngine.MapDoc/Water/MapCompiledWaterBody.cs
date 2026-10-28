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
/// drawing geometry and may share boundaries. Effective semantic polygons remain wide internally and the
/// triangles are their bounded drawing adapter.</summary>
public sealed record MapWaterRegion(string SpaceId, MapWaterRegionKind Kind, IReadOnlyList<MapExactTriangle> Triangles)
{
    /// <summary>The occupied owner and both boundary provenances for effective geometry.</summary>
    public MapOccupiedFragment? Fragment { get; init; }
    // Authoritative polygons remain wide. Triangles are the bounded drawing adapter only on the R5 path.
    internal IReadOnlyList<IReadOnlyList<MapWideXz>> WidePolygons { get; init; } = Array.Empty<IReadOnlyList<MapWideXz>>();
}

/// <summary>Conservative wet bounds of one occupied fragment, retaining its lower and upper owners.</summary>
public sealed record MapWaterFragmentBounds(MapOccupiedFragment Fragment, MapBox3 Bounds);

public enum MapWaterCompileStatus { Complete, Incomplete, CapacityExceeded, NotRepresentable, Invalid }

/// <summary>The counted work of one body's compile. <see cref="UpperFaces"/> and <see cref="LowerFaces"/> are the bound
/// faces read, <see cref="Triangles"/> the region triangles emitted.</summary>
public sealed record MapWaterWork(int Pieces, int PieceVertices, int FootprintRects, int FootprintCells, int BoundReads,
    int UpperFaces, int LowerFaces, int PairChecks, int Triangles)
{
    /// <summary>Bound-lattice slot candidates charged before bound reads, including absent, empty and skipped slots.
    /// Counts saturate at <see cref="int.MaxValue"/> when a preflight refuses a larger traversal.</summary>
    public int BoundSlots { get; init; }

    /// <summary>Bound-lattice cell candidates charged before read preflights and region sweeps, including absent,
    /// skipped and face-free cells. A sweep conservatively charges its part's rectangular lattice extent, including
    /// cells outside a clipped row. A refused preflight remains counted, saturating at <see cref="int.MaxValue"/>.</summary>
    public int BoundCells { get; init; }
}

/// <summary>The work one body's compile may spend. A null <see cref="Reads"/> means the released
/// <see cref="MapRefinementLimits"/> defaults, under which every bound read is chunked. <see cref="MaxPieces"/> and
/// <see cref="MaxPieceVertices"/> do not raise the fixed domain ceilings of 65,536 pieces and 262,144 vertices.
/// <see cref="MaxBoundCells"/> caps the cumulative sum of bound-lattice slot and cell candidates for bound reads and
/// region sweeps. Empty, absent, skipped and face-free candidates count too. Negative limits, including members of
/// <see cref="Reads"/>, throw <see cref="ArgumentOutOfRangeException"/> at compile entry, naming the limit.</summary>
/// <param name="MaxPieces">Demand sites: MapWaterWorkBudget and MapWaterWorkMeter. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxPieceVertices">Demand sites: MapWaterWorkBudget and MapWaterWorkMeter. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxFootprintCells">Demand sites: MapWaterWorkBudget and MapWaterWorkMeter. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxBoundFaces">Demand sites: MapWaterWorkBudget and MapWaterWorkMeter. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxPairChecks">Demand sites: MapWaterWorkBudget and MapWaterWorkMeter. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxTriangles">Demand sites: MapWaterWorkMeter.ChargeEmit. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="Reads">Refinement demand sites: MapBoundPreparation, MapBoundFaceContext and MapEffectiveGeometry.</param>
/// <param name="MaxBoundCells">Demand site: MapWaterWorkMeter.ChargeTraversal. See docs/CAPACITY-DEMAND.md.</param>
public sealed record MapWaterCompileLimits(int MaxPieces = MapWaterDomain.MaxPieces,
    int MaxPieceVertices = MapWaterDomain.MaxPieceVertices, int MaxFootprintCells = 4_194_304, int MaxBoundFaces = 1_048_576,
    int MaxPairChecks = (int)MapWaterDomain.MaxPairChecks, int MaxTriangles = 1_048_576, MapRefinementLimits? Reads = null,
    int MaxBoundCells = 4_194_304)
{
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxPieces, nameof(MaxPieces));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxPieceVertices, nameof(MaxPieceVertices));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxFootprintCells, nameof(MaxFootprintCells));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxBoundFaces, nameof(MaxBoundFaces));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxPairChecks, nameof(MaxPairChecks));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxTriangles, nameof(MaxTriangles));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxBoundCells, nameof(MaxBoundCells));
        if (Reads is not { } reads) return;
        ArgumentOutOfRangeException.ThrowIfNegative(reads.MaxSourceFacesPerBound, "Reads.MaxSourceFacesPerBound");
        ArgumentOutOfRangeException.ThrowIfNegative(reads.MaxPairChecks, "Reads.MaxPairChecks");
        ArgumentOutOfRangeException.ThrowIfNegative(reads.MaxRefinementFaces, "Reads.MaxRefinementFaces");
        ArgumentOutOfRangeException.ThrowIfNegative(reads.MaxRefinementVertices, "Reads.MaxRefinementVertices");
        ArgumentOutOfRangeException.ThrowIfNegative(reads.MaxContextPatches, "Reads.MaxContextPatches");
        ArgumentOutOfRangeException.ThrowIfNegative(reads.MaxContextFaces, "Reads.MaxContextFaces");
    }
}

/// <summary>One water body compiled against an acquisition: its regions split exactly at every ceiling contour, the
/// deepest bed under it, its status and counted work, and a digest that covers exactly what it read.
/// <see cref="DeepestBedY"/> and <see cref="Bounds"/> are null when no lower area resolved. A refused body keeps no
/// regions. <see cref="Medium"/> is usable only when <see cref="Status"/> is <see cref="MapWaterCompileStatus.Complete"/>.
/// An unsuccessful body may carry a placeholder medium with non-finite values.</summary>
public sealed record MapCompiledWaterBody(string BodyId, float SurfaceY, MapWaterMediumDoc Medium, string RenderMaterial,
    IReadOnlyList<MapWaterRegion> Regions, float? DeepestBedY, MapBox3? Bounds, MapWaterCompileStatus Status, string? Detail,
    MapWaterWork Work, string Digest)
{
    public Scene.MapSceneCapacity? Capacity { get; init; }

    /// <summary>The compiled domain, or null when it did not compile.</summary>
    internal MapWaterDomain? Domain { get; init; }

    /// <summary>The merged footprint rectangles of each covered space the compile gathered.</summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<MapWaterFootprintRect>> Footprints { get; init; } =
        new Dictionary<string, IReadOnlyList<MapWaterFootprintRect>>(StringComparer.Ordinal);

    internal IReadOnlyList<MapWaterClosedContour> ClosedContours { get; init; } = Array.Empty<MapWaterClosedContour>();
    public IReadOnlyList<MapWaterFragmentBounds> FragmentBounds { get; init; } = Array.Empty<MapWaterFragmentBounds>();
    internal IReadOnlyList<MapOccupiedPiece> OccupiedPieces { get; init; } = Array.Empty<MapOccupiedPiece>();
    internal IReadOnlyDictionary<string, IReadOnlyList<MapOccupiedPiece>> FragmentFootprints { get; init; } =
        new Dictionary<string, IReadOnlyList<MapOccupiedPiece>>(StringComparer.Ordinal);

    /// <summary>The kind of <paramref name="point"/> in the covered space <paramref name="spaceId"/>, or null when the
    /// body's domain does not hold it under OA7 or that space's footprint does not own it, the footprint owning a point
    /// exactly as <see cref="MapSpaceGeometry"/> assigns it to a lattice cell. Within the space a point any closed
    /// triangle or preserved tangential contour holds, boundaries included, is <see cref="MapWaterRegionKind.Closed"/>, so a contour tie closes as
    /// <see cref="MapWaterQuery"/> closes it. Otherwise a point a free triangle holds is
    /// <see cref="MapWaterRegionKind.FreeSurface"/>, and any other owned point is
    /// <see cref="MapWaterRegionKind.Unresolved"/>. With effective occupied fragments it returns their unanimous
    /// answer, or Unresolved when their kinds differ. Closed precedence never crosses fragments.</summary>
    public MapWaterRegionKind? Classify(string spaceId, MapExactXz point)
    {
        ArgumentNullException.ThrowIfNull(spaceId);
        if (Domain is null || !Domain.Contains(point)) return null;
        if (OccupiedPieces.Count != 0)
        {
            var kinds = OccupiedPieces.Where(p => p.SpaceId == spaceId && Owns(p, point))
                .Select(p => p.FragmentId).Distinct(StringComparer.Ordinal).Select(id => ClassifyFragment(id, point)).Distinct().ToArray();
            return kinds.Length == 0 ? null : kinds.Length == 1 ? kinds[0] : MapWaterRegionKind.Unresolved;
        }
        if (!Footprints.TryGetValue(spaceId, out IReadOnlyList<MapWaterFootprintRect>? rects) || !rects.Any(r => Owns(r, point))) return null;
        MapWideXz p = MapWideXz.From(point);
        if (Holds(MapWaterRegionKind.Closed)) return MapWaterRegionKind.Closed;
        if (!Holds(MapWaterRegionKind.FreeSurface)) return MapWaterRegionKind.Unresolved;
        return ClosedContours.Any(c => c.SpaceId == spaceId && c.Holds(p)) ? MapWaterRegionKind.Closed : MapWaterRegionKind.FreeSurface;

        bool Holds(MapWaterRegionKind kind) => Regions.Where(r => r.SpaceId == spaceId && r.Kind == kind)
            .SelectMany(r => r.Triangles).Any(t => MapWaterRegionGeometry.TriangleHolds(t, p));
    }

    /// <summary>Classifies one vertical fragment. Closed precedence applies within that fragment only.</summary>
    public MapWaterRegionKind? ClassifyFragment(string fragmentId, MapExactXz point)
    {
        ArgumentNullException.ThrowIfNull(fragmentId);
        if (Domain is null || !Domain.Contains(point) || !FragmentFootprints.TryGetValue(fragmentId, out var pieces) || !pieces.Any(p => Owns(p, point))) return null;
        var xz = MapWideXz.From(point);
        bool Holds(MapWaterRegionKind kind) => Regions.Where(r => r.Fragment?.Id == fragmentId && r.Kind == kind)
            .Any(r => MapWaterRegionGeometry.Holds(r, xz));
        if (Holds(MapWaterRegionKind.Closed) || ClosedContours.Any(c => c.FragmentId == fragmentId && c.Holds(xz))) return MapWaterRegionKind.Closed;
        return Holds(MapWaterRegionKind.FreeSurface) ? MapWaterRegionKind.FreeSurface : MapWaterRegionKind.Unresolved;
    }

    static bool Owns(MapOccupiedPiece piece, MapExactXz point) => MapWaterPolygons.RingContains(piece.Polygon, MapWideXz.From(point));

    static bool Owns(MapWaterFootprintRect rect, MapExactXz point)
    {
        var (x, z) = MapSpaceGeometry.Cell(rect.Frame, point);
        return x >= rect.CellMinX && x < rect.CellMaxX && z >= rect.CellMinZ && z < rect.CellMaxZ;
    }
}
