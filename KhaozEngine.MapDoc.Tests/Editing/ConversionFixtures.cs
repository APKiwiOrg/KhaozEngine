using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc;

internal static class ConversionFixtures
{
    static readonly MapSurfaceCell Full = new(1, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto);
    static readonly MapLatticeFrame Frame = new(new(1, 1), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);

    internal static MapSurfaceSet Slopes(MapOverlayCut centreCut = MapOverlayCut.Full, byte centreRotation = 0,
        bool coplanarCentre = false, MapOverlayCut southCut = MapOverlayCut.Full, byte southRotation = 0)
    {
        var set = new MapSurfaceSet();
        set.Refs.Add(Surface("coarse", Frame, MapSurfaceRole.SupportFloor));
        MapSurfacePatch patch = Patch(new("coarse", 0, 0), 0, 0, 3, 3, new[]
        {
            0, 100, 200, 300,
            50, 160, 250, 360,
            100, 210, 330, 400,
            150, 260, 380, 500,
        });
        if (coplanarCentre) patch.Heights[2 * 4 + 2] = 300;
        patch.Cells[1 * 3 + 1] = new(1, 7, centreCut, centreRotation,
            MapCellFlags.FeatherOverlay | MapCellFlags.Blocked, MapCellTopology.Auto);
        patch.Cells[1] = new(1, 3, southCut, southRotation, MapCellFlags.None, MapCellTopology.Auto);
        set.Patches.Add(patch.Key, patch);
        RequireValid(set);
        return set;
    }

    internal static MapFinePatchRequest Request(int k, bool accept)
        => new("coarse", new[] { new MapCellRect(1, 1, 2, 2) }, k, "coarse-fine-1", accept);

    internal static (MapSurfaceRef Surface, MapSurfacePatch Patch) Coarse(MapSurfaceSet set)
        => (set.Refs.Single(s => s.Id == "coarse"), set.Patches[new("coarse", 0, 0)]);

    internal static MapSurfacePatch Fine(MapConversionResult result)
    {
        MapRational coarseUnit = Coarse(result.Candidate).Surface.Frame.CellUnitMetres;
        MapRational fineUnit = FineSurface(result).Frame.CellUnitMetres;
        MapExactValue k = new MapExactValue(coarseUnit.Numerator, coarseUnit.Denominator)
            .Divide(new(fineUnit.Numerator, fineUnit.Denominator));
        if (k.Denominator != 1) throw new InvalidOperationException("fine fixture subdivision is not integral");
        return result.Candidate.Patches[MapPatchKey.ForCell("coarse-fine-1", k.Numerator, k.Numerator)];
    }

    internal static MapSurfaceRef FineSurface(MapConversionResult result)
        => result.Candidate.Refs.Single(s => s.Id == "coarse-fine-1");

    internal static IReadOnlyList<int> FineCorners(MapSurfacePatch patch)
    {
        var heights = new List<int>((patch.Width + 1) * (patch.Depth + 1));
        for (int z = 0; z <= patch.Depth; z++)
            for (int x = 0; x <= patch.Width; x++)
                heights.Add(patch.Height(x, z));
        return heights;
    }

    internal static MapSurfaceCell FineCell(MapSurfacePatch patch, int x, int z)
        => patch.Cells[z * patch.Width + x];

    internal static MapExactValue ExactHeight(MapSurfaceSet set, MapExactValue x, MapExactValue z)
    {
        MapExactValue? highest = null;
        foreach (MapSurfacePatch patch in set.Patches.Values)
        {
            MapSurfaceRef surface = set.Refs.Single(s => s.Id == patch.Key.SurfaceId);
            if (surface.Role != MapSurfaceRole.SupportFloor) continue;
            MapExactValue? height = MapSurfaceCompiler.ExactHeight(surface, patch, x, z);
            if (height is { } value && (highest is null || value.CompareTo(highest.Value) > 0)) highest = value;
        }
        return highest ?? throw new InvalidOperationException("conversion fixture has no support at the exact sample");
    }

    internal static SortedDictionary<ushort, MapExactValue> OverlayArea(MapSurfaceSet set)
    {
        var areas = new SortedDictionary<ushort, MapExactValue>();
        foreach (MapSurfacePatch patch in set.Patches.Values)
        {
            MapSurfaceRef surface = set.Refs.Single(s => s.Id == patch.Key.SurfaceId);
            MapCompiledPatch mesh = MapSurfaceCompiler.Compile(surface, patch);
            for (int i = 0; i < mesh.Faces.Count; i++)
            {
                MapPaintCoverage paint = mesh.Paint[i];
                if (!paint.OverlayCovers) continue;
                MapExactTriangle triangle = mesh.ExactTriangle(mesh.Faces[i]);
                MapExactValue twiceArea = triangle.B.X.Subtract(triangle.A.X)
                    .Multiply(triangle.C.Z.Subtract(triangle.A.Z))
                    .Subtract(triangle.C.X.Subtract(triangle.A.X)
                        .Multiply(triangle.B.Z.Subtract(triangle.A.Z)));
                MapExactValue area = (twiceArea.Sign < 0 ? twiceArea.Negate() : twiceArea).Divide(new(2, 1));
                areas.TryGetValue(paint.Overlay, out MapExactValue previous);
                areas[paint.Overlay] = previous.Add(area);
            }
        }
        return areas;
    }

    internal static MapSurfaceSeam RimSeam(MapConversionResult result, MapCellEdge edge)
    {
        (MapLatticeAddress from, MapLatticeAddress to) = edge switch
        {
            MapCellEdge.South => (MapLatticeAddress.Corner(1, 1), MapLatticeAddress.Corner(2, 1)),
            MapCellEdge.East => (MapLatticeAddress.Corner(2, 1), MapLatticeAddress.Corner(2, 2)),
            MapCellEdge.North => (MapLatticeAddress.Corner(1, 2), MapLatticeAddress.Corner(2, 2)),
            MapCellEdge.West => (MapLatticeAddress.Corner(1, 1), MapLatticeAddress.Corner(1, 2)),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
        return result.Candidate.AllRecords().OfType<MapSurfaceSeam>().Single(s => Matches(s.First) || Matches(s.Second));

        bool Matches(MapSurfaceEdgeRef side) => side.Patch == new MapPatchKey("coarse", 0, 0) &&
            side.From.SurfaceId == "coarse" && side.To.SurfaceId == "coarse" &&
            ((side.From.Address == from && side.To.Address == to) || (side.From.Address == to && side.To.Address == from));
    }

    internal static MapExactValue FractionAlongCoarseEdge(MapConversionResult result, MapLatticeVertex vertex)
    {
        // This fixture samples the south rim [1, 2] at z = 1 in increasing world X.
        MapSurfaceRef surface = result.Candidate.Refs.Single(s => s.Id == vertex.SurfaceId);
        MapExactXz point = surface.Frame.WorldXz(vertex.Address);
        if (point.Z != new MapExactValue(1, 1) || point.X.CompareTo(new(1, 1)) < 0 || point.X.CompareTo(new(2, 1)) > 0)
            throw new InvalidOperationException("conversion fixture vertex is outside the south coarse rim");
        return point.X.Subtract(new(1, 1));
    }

    internal static MapSurfaceSet WithCaveCeiling()
    {
        MapSurfaceSet set = Slopes();
        set.Refs.Add(Surface("roof", Frame, MapSurfaceRole.Ceiling));
        MapSurfacePatch roof = Patch(new("roof", 0, 0), 1, 1, 1, 1, new[] { 900, 900, 900, 900 });
        roof.EdgeSubdivisions.AddRange(new[]
        {
            new MapEdgeSubdivision(0, 0, MapCellEdge.South, 3),
            new(0, 0, MapCellEdge.East, 3),
            new(0, 0, MapCellEdge.North, 3),
            new(0, 0, MapCellEdge.West, 3),
        });
        set.Patches.Add(roof.Key, roof);
        MapSurfacePatch coarse = Coarse(set).Patch;
        coarse.Records.Add(Space("room", MapSpaceKind.Cave));
        coarse.Records.Add(new MapSpaceFootprint("room-cells", new("room", coarse.Key), coarse.Key,
            new[] { 1 * 64 + 1 }, new(MapBoundKind.SupportFloor, "coarse", null), new(MapBoundKind.Ceiling, "roof", null)));
        RequireValid(set);
        return set;
    }

    internal static MapSurfaceSet WithWallOnTheCentreEdge()
    {
        MapSurfaceSet set = Slopes();
        MapSurfacePatch patch = Coarse(set).Patch;
        patch.Records.Add(new MapBoundaryChain("west-foot", MapChainKind.SurfaceEdge, patch.Key, new[]
        {
            new MapChainVertex(new("coarse", MapLatticeAddress.Corner(1, 1)), null),
            new MapChainVertex(new("coarse", MapLatticeAddress.Corner(1, 2)), null),
        }));
        RequireValid(set);
        return set;
    }

    internal static MapSurfaceSet LegacyRowWithNoDraw()
    {
        var set = new MapSurfaceSet();
        set.Refs.Add(Surface("plane-0", MapLatticeFrame.ImportedMetreCentimetre,
            MapSurfaceRole.SupportFloor, MapPresencePolicy.LegacyTileWorld));
        MapSurfacePatch patch = Patch(new("plane-0", 0, 0), 0, 0, 2, 1, new int[6]);
        patch.Cells[1] = Full with { Flags = MapCellFlags.NoDraw };
        patch.Records.Add(Space("world", MapSpaceKind.Exterior));
        patch.Records.Add(new MapSpaceFootprint("world-cells", new("world", patch.Key), patch.Key, new[] { 0, 1 },
            new(MapBoundKind.LegacyExteriorV1, "plane-0", null), new(MapBoundKind.OpenTop, null, null)));
        set.Patches.Add(patch.Key, patch);
        RequireValid(set);
        return set;
    }

    internal static (MapSurfaceRef Surface, MapSurfacePatch Patch) Legacy(MapSurfaceSet set)
        => (set.Refs.Single(s => s.Id == "plane-0"), set.Patches[new("plane-0", 0, 0)]);

    internal static MapFinePatchRequest LegacyRequest(int k, bool accept)
        => new("plane-0", new[] { new MapCellRect(0, 0, 2, 1) }, k, "plane-0-fine-1", accept);

    internal static MapSurfaceSet MixedPorch()
    {
        var set = new MapSurfaceSet();
        set.Refs.Add(Surface("half-floor", new(new(1, 2), new(1, 200), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0),
            MapSurfaceRole.SupportFloor));
        set.Refs.Add(Surface("third-roof", new(new(1, 3), new(1, 300), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0),
            MapSurfaceRole.Ceiling));
        MapSurfacePatch floor = Patch(new("half-floor", 0, 0), 0, 0, 2, 2, new int[9]);
        MapSurfacePatch roof = Patch(new("third-roof", 0, 0), 0, 0, 3, 3, Enumerable.Repeat(900, 16).ToArray());
        floor.Records.Add(Space("porch", MapSpaceKind.Exterior));
        floor.Records.Add(new MapSpaceFootprint("porch-cells", new("porch", floor.Key), floor.Key, new[] { 0, 1, 64, 65 },
            new(MapBoundKind.SupportFloor, "half-floor", null), new(MapBoundKind.Ceiling, "third-roof", null)));
        set.Patches.Add(floor.Key, floor);
        set.Patches.Add(roof.Key, roof);
        RequireValid(set);
        return set;
    }

    internal static MapFinePatchRequest PorchRoofRequest(int width)
        => new("third-roof", new[] { new MapCellRect(0, 0, width, 3) }, 2, "third-roof-fine-1", false);

    static MapSurfaceRef Surface(string id, MapLatticeFrame frame, MapSurfaceRole role,
        MapPresencePolicy policy = MapPresencePolicy.Native)
        => new(id, frame, role, policy, null, null, "");

    static MapSpaceDoc Space(string id, MapSpaceKind kind)
        => new(id, kind, null, null, Array.Empty<string>(), Array.Empty<MapBoundaryRef>(),
            Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>());

    static MapSurfacePatch Patch(MapPatchKey key, int minX, int minZ, int width, int depth, int[] heights)
    {
        var patch = new MapSurfacePatch
        {
            Key = key,
            CellMinX = minX,
            CellMinZ = minZ,
            Width = width,
            Depth = depth,
            Heights = heights,
            Cells = Enumerable.Repeat(Full, width * depth).ToArray(),
            Presence = new ulong[(width * depth + 63) / 64],
        };
        for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
                patch.SetPresent(x, z, true);
        return patch;
    }

    static void RequireValid(MapSurfaceSet set)
    {
        foreach (MapSurfacePatch patch in set.Patches.Values)
        {
            IReadOnlyList<string> local = patch.ValidateLocal();
            if (local.Count != 0)
                throw new InvalidOperationException("conversion fixture local validation failed: " + string.Join(", ", local));
        }
        IReadOnlyList<string> references = MapTopologyReferenceValidator.Validate(set.Refs, set.Patches.Values.ToArray());
        if (references.Count != 0)
            throw new InvalidOperationException("conversion fixture reference validation failed: " + string.Join(", ", references));
        foreach (MapSpaceFootprint footprint in set.AllRecords().OfType<MapSpaceFootprint>())
        {
            if (!set.TryGetRecord(footprint.Space, out MapTopologyRecord? record) || record is not MapSpaceDoc space)
                throw new InvalidOperationException("conversion fixture space is unresolved");
            MapSurfaceRef? lower = set.Refs.SingleOrDefault(s => s.Id == footprint.Lower.SurfaceId);
            if (MapLegacyExteriorRecipe.Check(footprint, space, lower) is { } failure)
                throw new InvalidOperationException("conversion fixture " + failure);
        }
        foreach (MapSurfacePatch patch in set.Patches.Values)
            _ = MapSurfaceCompiler.Compile(set.Refs.Single(s => s.Id == patch.Key.SurfaceId), patch);
        MapScopedSurfaces view = MapScopedSurfaces.CompleteView(set);
        foreach (MapBoundaryChain chain in set.AllRecords().OfType<MapBoundaryChain>())
        {
            MapChainResolution resolution = MapBoundaryGeometry.ResolveChain(chain, view);
            if (resolution.Status != MapResolveStatus.Resolved)
                throw new InvalidOperationException("conversion fixture chain failed: " + resolution.Detail);
        }
    }
}
