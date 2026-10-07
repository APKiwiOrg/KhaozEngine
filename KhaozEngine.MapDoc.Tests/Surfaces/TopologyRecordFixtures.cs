using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc;

internal sealed record TopologyWorld(List<MapSurfaceRef> Surfaces, List<MapSurfacePatch> Patches)
{
    internal IReadOnlyList<string> Validate() => MapTopologyReferenceValidator.Validate(Surfaces, Patches);
}

internal static class TopologyRecordFixtures
{
    static readonly MapPatchKey Floor00 = new("floor", 0, 0), Ceiling00 = new("ceiling", 0, 0);
    static readonly MapLatticeFrame Frame = new(new(1, 1), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);

    internal static TopologyWorld TwoSpacesSharingOneStrip()
    {
        MapSurfacePatch floor = SurfacePatchFixtures.Row(2);
        floor.Key = Floor00;
        MapSurfacePatch ceiling = SurfacePatchFixtures.Row(2);
        ceiling.Key = Ceiling00;
        Array.Fill(ceiling.Heights, 300);
        floor.Records.Add(Space("a", MapSpaceKind.Cave, new[] { new MapBoundaryRef(new("wall", Floor00), MapSide.Front) }));
        floor.Records.Add(Space("b", MapSpaceKind.Cave, new[] { new MapBoundaryRef(new("wall", Floor00), MapSide.Back) }));
        floor.Records.Add(new MapSpaceFootprint("fragment-a", new("a", Floor00), Floor00, new[] { 0 },
            new(MapBoundKind.SupportFloor, "floor", null), new(MapBoundKind.Ceiling, "ceiling", null)));
        floor.Records.Add(new MapSpaceFootprint("fragment-b", new("b", Floor00), Floor00, new[] { 1 },
            new(MapBoundKind.SupportFloor, "floor", null), new(MapBoundKind.Ceiling, "ceiling", null)));
        floor.Records.Add(EdgeChain("lower", Floor00, 1, 0, 1, 1));
        floor.Records.Add(EdgeChain("upper", Ceiling00, 1, 0, 1, 1));
        floor.Records.Add(new MapWallStrip("wall", new("lower", Floor00), new("upper", Floor00), MapStripFacing.TwoSided, 1));
        return new(new()
        {
            new("floor", Frame, MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, ""),
            new("ceiling", Frame, MapSurfaceRole.Ceiling, MapPresencePolicy.Native, null, null, ""),
        }, new() { floor, ceiling });
    }

    internal static TopologyWorld LegacyExteriorWorld()
    {
        MapSurfacePatch patch = SurfacePatchFixtures.Row(2);
        patch.Key = new("plane-0", 0, 0);
        patch.Records.Add(Space("world", MapSpaceKind.Exterior, Array.Empty<MapBoundaryRef>()));
        patch.Records.Add(new MapSpaceFootprint("world-cells", new("world", patch.Key), patch.Key, new[] { 0, 1 },
            new(MapBoundKind.LegacyExteriorV1, "plane-0", null), new(MapBoundKind.OpenTop, null, null)));
        return new(new()
        {
            new("plane-0", MapLatticeFrame.ImportedMetreCentimetre, MapSurfaceRole.SupportFloor,
                MapPresencePolicy.LegacyTileWorld, null, null, ""),
        }, new() { patch });
    }

    internal static void ReplaceWall(TopologyWorld world, string spaceId, MapBoundaryRef wall)
        => Replace<MapSpaceDoc>(world, spaceId, space => space with { Walls = new[] { wall } });

    internal static void SetUpper(TopologyWorld world, string footprintId, MapBoundRef bound)
        => Replace<MapSpaceFootprint>(world, footprintId, footprint => footprint with { Upper = bound });

    internal static void SetLowerKind(TopologyWorld world, string footprintId, MapBoundKind kind)
        => Replace<MapSpaceFootprint>(world, footprintId, footprint => footprint with { Lower = footprint.Lower with { Kind = kind } });

    internal static void SetLowerSurface(TopologyWorld world, string footprintId, string surfaceId, MapLatticeFrame frame)
    {
        if (!world.Surfaces.Any(s => s.Id == surfaceId))
            world.Surfaces.Add(new(surfaceId, frame, MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, ""));
        Replace<MapSpaceFootprint>(world, footprintId, footprint => footprint with
        {
            Lower = footprint.Lower with { SurfaceId = surfaceId },
        });
    }

    internal static void OpeningOverPresentCell(TopologyWorld world)
    {
        MapSurfacePatch floor = world.Patches.Single(p => p.Key == Floor00);
        floor.Records.Add(new MapHorizontalOpening("opening", Floor00, new[] { 0 }));
    }

    internal static MapSurfacePatch SampleWithRecords()
    {
        MapSurfacePatch patch = SurfacePatchFixtures.Sample();
        patch.Records.Add(new MapWallStrip("strip-b", new("lower", patch.Key), new("upper", patch.Key), MapStripFacing.TwoSided, 1));
        patch.Records.Add(EdgeChain("lower", patch.Key, -4, 0, -3, 0));
        patch.Records.Add(new MapBoundaryChain("upper", MapChainKind.Authored, null, new[]
        {
            new MapChainVertex(new("ground", MapLatticeAddress.Corner(-4, 0)), 300),
            new MapChainVertex(new("ground", MapLatticeAddress.Corner(-3, 0)), 300),
        }));
        return patch;
    }

    internal static TopologyWorld WithEveryRecordKind()
    {
        TopologyWorld world = TwoSpacesSharingOneStrip();
        MapSurfacePatch floor = world.Patches.Single(p => p.Key == Floor00);
        floor.SetPresent(0, 0, false);
        var f0 = new MapLatticeVertex("floor", MapLatticeAddress.Corner(0, 0));
        var f1 = new MapLatticeVertex("floor", MapLatticeAddress.Corner(1, 0));
        var c0 = new MapLatticeVertex("ceiling", MapLatticeAddress.Corner(0, 0));
        var c1 = new MapLatticeVertex("ceiling", MapLatticeAddress.Corner(1, 0));
        floor.Records.Add(new MapSurfaceSeam("seam", new(Floor00, f0, f1), new(Ceiling00, c0, c1),
            new[] { (f0, c0), (f1, c1) }));
        floor.Records.Add(new MapHorizontalOpening("opening", Floor00, new[] { 0 }));
        floor.Records.Add(new MapCavePortal("portal", new("a", Floor00), new("b", Floor00),
            new[] { f0, f1 }, new("lower", Floor00), new("upper", Floor00)));
        floor.Records.Add(new MapVerticalLink("link", new("a", Floor00), new("b", Floor00),
            new[] { new MapRecordRef("opening", Floor00) }, new[] { new MapRecordRef("portal", Floor00) },
            new[] { new MapRecordRef("wall", Floor00) }));
        Replace<MapSpaceDoc>(world, "a", space => space with
        {
            Portals = new[] { new MapBoundaryRef(new("portal", Floor00), MapSide.Front) },
            Links = new[] { new MapRecordRef("link", Floor00) },
        });
        Replace<MapSpaceDoc>(world, "b", space => space with
        {
            Portals = new[] { new MapBoundaryRef(new("portal", Floor00), MapSide.Back) },
            Links = new[] { new MapRecordRef("link", Floor00) },
        });
        return world;
    }

    static MapSpaceDoc Space(string id, MapSpaceKind kind, IReadOnlyList<MapBoundaryRef> walls)
        => new(id, kind, null, null, Array.Empty<string>(), walls, Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>());

    static MapBoundaryChain EdgeChain(string id, MapPatchKey source, long x0, long z0, long x1, long z1)
        => new(id, MapChainKind.SurfaceEdge, source, new[]
        {
            new MapChainVertex(new(source.SurfaceId, MapLatticeAddress.Corner(x0, z0)), null),
            new MapChainVertex(new(source.SurfaceId, MapLatticeAddress.Corner(x1, z1)), null),
        });

    static void Replace<T>(TopologyWorld world, string id, Func<T, T> replace) where T : MapTopologyRecord
    {
        foreach (MapSurfacePatch patch in world.Patches)
        {
            int index = patch.Records.FindIndex(r => r.Id == id);
            if (index >= 0) { patch.Records[index] = replace((T)patch.Records[index]); return; }
        }
        throw new InvalidOperationException("synthetic record missing");
    }
}
