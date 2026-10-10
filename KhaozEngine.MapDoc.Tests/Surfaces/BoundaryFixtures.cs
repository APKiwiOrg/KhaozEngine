using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc;

internal static class BoundaryFixtures
{
    static readonly MapSurfaceCell Full = new(1, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto);

    internal static MapCompiledStrip CompileStrip(int[] lower, int[] upper, MapStripFacing facing)
    {
        MapSurfaceRef surface = Surface("s");
        var key = new MapPatchKey("s", 0, 0);
        MapSurfacePatch patch = RowPatch(key, 0, 0, new[] { 0, 0 }, new[] { 0, 0 });
        var lowerChain = new MapBoundaryChain("lower", MapChainKind.Authored, null,
            lower.Select((height, x) => new MapChainVertex(new("s", MapLatticeAddress.Corner(x, 0)), height)).ToArray());
        var upperChain = new MapBoundaryChain("upper", MapChainKind.Authored, null,
            upper.Select((height, x) => new MapChainVertex(new("s", MapLatticeAddress.Corner(x, 0)), height)).ToArray());
        var strip = new MapWallStrip("w", new("lower", key), new("upper", key), facing, 1);
        patch.Records.Add(lowerChain);
        patch.Records.Add(upperChain);
        patch.Records.Add(strip);
        var set = new MapSurfaceSet();
        set.Refs.Add(surface);
        set.Patches.Add(key, patch);
        MapScopedSurfaces view = CompleteView(set);
        MapChainResolution resolvedLower = MapBoundaryGeometry.ResolveChain(lowerChain, view);
        MapChainResolution resolvedUpper = MapBoundaryGeometry.ResolveChain(upperChain, view);
        return MapWallStripCompiler.Compile(strip, resolvedLower, resolvedUpper);
    }

    internal static (MapScopedSurfaces View, MapSurfaceSeam Seam) CoarseFineSeam(int k, bool subdivided, int middleOffset)
    {
        var (set, _, _, seam) = CoarseFineSet(k, subdivided, middleOffset);
        return (CompleteView(set), seam);
    }

    internal static (MapScopedSurfaces View, (MapSurfaceRef Surface, MapSurfacePatch Patch) Neg,
        (MapSurfaceRef Surface, MapSurfacePatch Patch) Pos) NegativeSlotPair()
    {
        MapSurfaceRef surface = Surface("neg");
        MapSurfacePatch neg = RowPatch(new("neg", -1, 0), 63, 0, new[] { 10, 20 }, new[] { 30, 40 });
        MapSurfacePatch pos = RowPatch(new("neg", 0, 0), 0, 0, new[] { 20, 50 }, new[] { 40, 60 });
        pos.CornerDependencies.Add(new(0, 0, new(neg.Key, MapLatticeAddress.Corner(0, 0))));
        pos.CornerDependencies.Add(new(0, 1, new(neg.Key, MapLatticeAddress.Corner(0, 1))));
        var set = new MapSurfaceSet();
        set.Refs.Add(surface);
        set.Patches.Add(neg.Key, neg);
        set.Patches.Add(pos.Key, pos);
        return (CompleteView(set), (surface, neg), (surface, pos));
    }

    internal static (MapScopedSurfaces View, MapSurfacePatch Fine) FineCornerOnCoarseRim()
    {
        var (set, coarse, fine, _) = CoarseFineSet(3, true, 0);
        fine.CornerDependencies.Add(new(1, 0, new(coarse.Key, MapLatticeAddress.Create(1, 3, 3))));
        return (CompleteView(set), fine);
    }

    internal static MapScopedSurfaces ViewWithoutCoarse()
    {
        var (set, coarse, fine, _) = CoarseFineSet(3, true, 0);
        fine.CornerDependencies.Add(new(1, 0, new(coarse.Key, MapLatticeAddress.Create(1, 3, 3))));
        set.Patches.Remove(coarse.Key);
        return CompleteView(set);
    }

    internal static (MapSurfaceRef Surface, MapSurfacePatch Patch, MapHorizontalOpening Opening) TwoCellOpening()
    {
        MapSurfaceRef surface = Surface("o");
        MapSurfacePatch patch = RowPatch(new("o", 0, 0), 0, 0,
            new[] { 400, 400, 400, 400 }, new[] { 400, 400, 400, 400 });
        patch.SetPresent(0, 0, false);
        patch.SetPresent(1, 0, false);
        var opening = new MapHorizontalOpening("hole", patch.Key, new[] { 0, 1 });
        patch.Records.Add(opening);
        var set = new MapSurfaceSet();
        set.Refs.Add(surface);
        set.Patches.Add(patch.Key, patch);
        RequireValid(set);
        return (surface, patch, opening);
    }

    static (MapSurfaceSet Set, MapSurfacePatch Coarse, MapSurfacePatch Fine, MapSurfaceSeam Seam)
        CoarseFineSet(int k, bool subdivided, int middleOffset)
    {
        MapSurfacePatch coarse = RowPatch(new("coarse", 0, 0), 0, 0, new[] { 100, 200 }, new[] { 100, 200 });
        if (subdivided) coarse.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.North, k));
        int[] heights = Enumerable.Range(0, k + 1).Select(i => 100 * k + 100 * i + (i == 1 ? middleOffset : 0)).ToArray();
        MapSurfacePatch fine = RowPatch(MapPatchKey.ForCell("fine", 0, k), 0, k % MapPatchKey.SlotCells, heights, heights);
        var pairs = Enumerable.Range(0, k + 1).Select(i =>
            (First: new MapLatticeVertex("coarse", MapLatticeAddress.Create(i, k, k)),
             Second: new MapLatticeVertex("fine", MapLatticeAddress.Corner(i, k)))).ToArray();
        var seam = new MapSurfaceSeam("coarse-fine-seam",
            new(coarse.Key, pairs[0].First, pairs[^1].First),
            new(fine.Key, pairs[0].Second, pairs[^1].Second), pairs);
        coarse.Records.Add(seam);
        var set = new MapSurfaceSet();
        set.Refs.Add(Surface("coarse"));
        set.Refs.Add(Surface("fine", k));
        set.Patches.Add(coarse.Key, coarse);
        set.Patches.Add(fine.Key, fine);
        return (set, coarse, fine, seam);
    }

    static MapSurfaceRef Surface(string id, int k = 1) => new(id,
        new(new(1, k), new(1, 100 * k), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0),
        MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, "");

    static MapSurfacePatch RowPatch(MapPatchKey key, int minX, int minZ, int[] south, int[] north)
    {
        int width = south.Length - 1;
        var patch = new MapSurfacePatch
        {
            Key = key,
            CellMinX = minX,
            CellMinZ = minZ,
            Width = width,
            Depth = 1,
            Heights = south.Concat(north).ToArray(),
            Cells = Enumerable.Repeat(Full, width).ToArray(),
            Presence = new ulong[(width + 63) / 64],
        };
        for (int x = 0; x < width; x++) patch.SetPresent(x, 0, true);
        return patch;
    }

    static MapScopedSurfaces CompleteView(MapSurfaceSet set)
    {
        RequireValid(set);
        return MapScopedSurfaces.CompleteView(set);
    }

    static void RequireValid(MapSurfaceSet set)
    {
        foreach (MapSurfacePatch patch in set.Patches.Values)
        {
            IReadOnlyList<string> local = patch.ValidateLocal();
            if (local.Count != 0) throw new InvalidOperationException("boundary fixture local validation failed: " + string.Join(", ", local));
        }
        IReadOnlyList<string> references = MapTopologyReferenceValidator.Validate(set.Refs, set.Patches.Values.ToArray());
        if (references.Count != 0) throw new InvalidOperationException("boundary fixture reference validation failed: " + string.Join(", ", references));
    }
}
