using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc;

internal static class TerrainEditFixtures
{
    internal static readonly MapPatchKey BumpKey = new("bump", 0, 0);

    internal static MapSurfaceSet Bump()
    {
        var set = new MapSurfaceSet();
        set.Refs.Add(new("bump", new(new(1, 1), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0),
            MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, ""));
        var heights = new int[25];
        heights[2 * 5 + 2] = 9;
        set.Patches.Add(BumpKey, new MapSurfacePatch
        {
            Key = BumpKey,
            Width = 4,
            Depth = 4,
            Heights = heights,
            Cells = Enumerable.Repeat(new MapSurfaceCell(1, 0, MapOverlayCut.Full, 0,
                MapCellFlags.None, MapCellTopology.Auto), 16).ToArray(),
            Presence = new[] { (1UL << 16) - 1 },
        });
        return set;
    }

    internal static IReadOnlyList<(int X, int Z)> Ring(int x, int z) =>
        Enumerable.Range(z - 1, 3).SelectMany(row => Enumerable.Range(x - 1, 3)
            .Where(column => column != x || row != z).Select(column => (X: column, Z: row))).ToArray();

    internal static string[] Digest(MapSurfaceSet set) => set.Patches.OrderBy(p => p.Key)
        .Select(p => MapSurfaceSemantics.PatchDigest(p.Value)).ToArray();

    internal static MapSurfacePatch GroundBelowSlot() => new()
    {
        Key = new("ground", 0, -1),
        CellMinX = 60,
        CellMinZ = 60,
        Width = 4,
        Depth = 4,
        Heights = Enumerable.Repeat(1000, 25).ToArray(),
        Cells = Enumerable.Repeat(new MapSurfaceCell(1, 0, MapOverlayCut.Full, 0,
            MapCellFlags.None, MapCellTopology.Auto), 16).ToArray(),
        Presence = new[] { (1UL << 16) - 1 },
    };

    internal static MapReassignCornerOwner ReassignGroundColumn(MapSurfaceSet set)
    {
        var removed = new MapPatchKey("ground", 0, 0);
        var changes = new List<MapCornerOwnerChange>();
        foreach (MapSurfacePatch patch in set.Patches.Values.Where(p => p.Key != removed))
            foreach (MapCornerDependency dependency in patch.CornerDependencies.Where(d => d.Owner.Patch == removed))
            {
                MapLatticeAddress address = dependency.Owner.Address;
                var owner = new MapPatchKey("ground", address.Z == 0 ? 0 : 1, address.Z == 0 ? -1 : 0);
                changes.Add(new(patch.Key, dependency.CornerX, dependency.CornerZ, dependency.Owner, new(owner, address)));
            }
        return new(changes.ToArray());
    }

    internal static MapSurfaceSet WithFarAnchors()
    {
        MapSurfaceSet set = SurfaceStorageFixtures.ThreeSurfaces().Surfaces;
        var west = new MapPatchKey("far", -300, 0);
        var east = new MapPatchKey("far", 300, 0);
        MapLatticeVertex[] vertices =
        {
            new("far", MapLatticeAddress.Corner(19200, 0)),
            new("far", MapLatticeAddress.Corner(19201, 0)),
        };
        set.Patches[west].Records.Add(new MapBoundaryChain("far-chain", MapChainKind.Authored, null,
            vertices.Select(v => new MapChainVertex(v, 2000)).ToArray()));
        set.Patches[east].Records.Add(new MapBoundaryChain("far-edge", MapChainKind.SurfaceEdge, east,
            vertices.Select(v => new MapChainVertex(v, null)).ToArray()));
        set.Patches[east].Records.Add(new MapWallStrip("far-wall", new("far-edge", east), new("far-chain", west),
            MapStripFacing.Front, 1));
        return set;
    }

    internal static MapTerrainEdit Edit(string kind)
    {
        MapSurfaceCell cell = Bump().Patches[BumpKey].Cells[0];
        return kind switch
        {
            "heights" => new MapSetCornerHeights(BumpKey, 1, 1, 1, 1, new[] { 5 }),
            "ids" => new MapSetCells(BumpKey, 0, 0, 1, 1, new[] { cell with { Underlay = 2 } }),
            "cut" => new MapSetCells(BumpKey, 0, 0, 1, 1, new[] { cell with { Cut = MapOverlayCut.CornerQuarter, Overlay = 3 } }),
            _ => throw new ArgumentException("unknown terrain edit fixture", nameof(kind)),
        };
    }
}
