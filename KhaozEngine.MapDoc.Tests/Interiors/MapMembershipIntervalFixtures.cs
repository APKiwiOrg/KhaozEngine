using System;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Tests.MapDoc.Scene;

namespace KhaozEngine.Tests.MapDoc.Interiors;

internal static class MapMembershipIntervalFixtures
{
    internal static NativeSceneFixture Create(string shape)
    {
        var fixture = NativeSceneFixtures.Create("empty");
        var set = fixture.Document.Surfaces;
        bool mixed = shape == "mixed", closed = mixed || shape is "opening" or "prism" or "fragmented" or "clipped";
        var floorRef = MixedResolutionFixtures.Surface("floor", mixed ? 2 : 1, MapSurfaceRole.SupportFloor);
        if (shape == "pooled") floorRef = floorRef with
        { Frame = new(new(1, 1), new(1, 1), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0) };
        if (shape == "face-free") floorRef = floorRef with
        { Frame = MapLatticeFrame.ImportedMetreCentimetre, PresencePolicy = MapPresencePolicy.LegacyTileWorld };
        var floor = MixedResolutionFixtures.Patch(new("floor", shape == "pooled" ? 100000000 : 0, 0), 1, 1,
            (x, z) => shape == "pooled" ? x == 0 ? 0 : int.MaxValue : shape == "long" ? x * 1000000 + z * 1999999 : shape == "clipped" ? x * 400 : 0);
        floor.Cells[0] = floor.Cells[0] with
        { Topology = MapCellTopology.ForceSwNe, Underlay = shape == "face-free" ? (ushort)0 : (ushort)1 };
        set.Refs.Add(floorRef); set.Patches.Add(floor.Key, floor);
        if (shape is "subdivided" or "transformed")
            foreach (var edge in Enum.GetValues<MapCellEdge>()) floor.EdgeSubdivisions.Add(new(0, 0, edge, 4));
        if (shape == "transformed")
            fixture.Document.NativeResourceTransforms = [new(MapPrefabChildKind.Surface, "floor",
                new(.25, 0, .25, .4, 1), new string('a', 64))];
        MapBoundRef upper = new(MapBoundKind.OpenTop, null, null);
        if (closed)
        {
            var ceiling = MixedResolutionFixtures.Patch(new("ceiling", 0, 0), mixed ? 2 : 1, mixed ? 2 : 1,
                (_, _) => mixed ? 1200 : 600);
            foreach (int cell in Enumerable.Range(0, ceiling.Cells.Length))
                ceiling.Cells[cell] = ceiling.Cells[cell] with { Topology = MapCellTopology.ForceSwNe };
            set.Refs.Add(MixedResolutionFixtures.Surface("ceiling", mixed ? 4 : 1, MapSurfaceRole.Ceiling));
            set.Patches.Add(ceiling.Key, ceiling);
            upper = new(MapBoundKind.Ceiling, "ceiling", null);
            if (shape == "opening")
            {
                ceiling.SetPresent(0, 0, false);
                ceiling.Records.Add(new MapHorizontalOpening("opening", ceiling.Key, [0]));
                upper = new(MapBoundKind.HorizontalOpening, null, new("opening", ceiling.Key));
            }
        }
        floor.Records.Add(new MapSpaceDoc("world", MapSpaceKind.Exterior, null, null, [], [], [], []));
        floor.Records.Add(new MapSpaceFootprint("cells", new("world", floor.Key), floor.Key, [0],
            new(shape == "face-free" ? MapBoundKind.LegacyExteriorV1 : MapBoundKind.SupportFloor, "floor", null), upper));
        if (shape == "portal")
        {
            floor.Records.Add(new MapSpaceDoc("other", MapSpaceKind.Exterior, null, null, [], [], [], []));
            MapLatticeVertex a = new("floor", MapLatticeAddress.Corner(0, 0)), b = new("floor", MapLatticeAddress.Corner(1, 0));
            floor.Records.Add(new MapBoundaryChain("bottom", MapChainKind.Authored, null, [new(a, 0), new(b, 0)]));
            floor.Records.Add(new MapCavePortal("portal", new("world", floor.Key), new("other", floor.Key),
                [a, b], new("bottom", floor.Key), null));
        }
        if (shape is "prism" or "fragmented" or "clipped")
        {
            floor.Records.Add(new MapSpaceDoc("room", MapSpaceKind.Room, new("world", floor.Key), null, [], [], [], []));
            var frame = new MapLatticeFrame(new(1, 64), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);
            MapWaterRect[] rects = shape == "fragmented"
                ? Enumerable.Range(0, 32).Select(i => new MapWaterRect(2 * i, 0, 2 * i + 1, 64)).ToArray()
                : [new(0, 0, 64, 64)];
            floor.Records.Add(new MapPrismDomain("prism", 1, new("room", floor.Key), new("world", floor.Key),
                "", "level", new(frame, rects, []), 1, 5));
            fixture.Document.BuildingIntent = new(1, [], [], [], []) { Storeys = [new(1, "level", "Level", null)] };
        }
        for (int i = 0; i < set.Refs.Count; i++)
        {
            var surface = set.Refs[i];
            set.Refs[i] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                set.Patches.Where(p => p.Key.SurfaceId == surface.Id).Select(p =>
                    System.Collections.Generic.KeyValuePair.Create(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))))
            };
        }
        return fixture;
    }
}
