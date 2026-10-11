using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Water;

internal static partial class WaterFixtures
{
    /// <summary>An exterior <c>sea</c> over a floor at -10 m for x 0 to 8 and a cave <c>grotto</c> for x 8 to 12 with
    /// floor -10 m and roof +5 m, joined at x 8 by a wall portal whose band runs from <paramref name="portalBottomY"/>
    /// to +3 m. A null <paramref name="portalBottomY"/> omits the portal and closes the wall. The sea continues over
    /// the grotto's roof on <c>cliff-top</c>, so its side of the shared wall is covered to the open sky. Body
    /// <c>ocean</c> at <c>SurfaceY</c> 0 spans x 0 to 12 and covers <c>sea</c> and <c>grotto</c>.</summary>
    internal static MapDocument SeaConnectedCave(float? portalBottomY)
    {
        var set = new MapSurfaceSet();
        Surface(set, "sea-floor", MapSurfaceRole.SupportFloor, Frame, 0, 0, 8, 4, (_, _) => -1000);
        Surface(set, "grotto-floor", MapSurfaceRole.SupportFloor, Frame, 8, 0, 12, 4, (_, _) => -1000);
        Surface(set, "grotto-roof", MapSurfaceRole.Ceiling, Frame, 8, 0, 12, 4, (_, _) => 500);
        Surface(set, "cliff-top", MapSurfaceRole.SupportFloor, Frame, 8, 0, 12, 4, (_, _) => 500);

        Footprints(set, "sea", MapSpaceKind.Exterior, Frame, 0, 0, 8, 4, Floor("sea-floor"), OpenTop, Slot0("sea-floor"));
        FootprintCells(set, "sea", "sea-cliff", 8, 0, 12, 4, Floor("cliff-top"), OpenTop, Slot0("sea-floor"));
        Footprints(set, "grotto", MapSpaceKind.Cave, Frame, 8, 0, 12, 4, Floor("grotto-floor"), Ceiling("grotto-roof"), Slot0("grotto-floor"));
        CloseWalls(set, "grotto", "grotto-floor", "grotto-roof", 8, 0, 12, 4, west: false);

        // The west wall runs along lattice x 8, whose front normal points away from the grotto, so the sea owns the
        // front side of every two-sided strip and the grotto the back.
        MapSurfacePatch anchor = set.Patches[Slot0("grotto-floor")];
        var seaWalls = new List<MapBoundaryRef>();
        var grottoWalls = new List<MapBoundaryRef>();
        MapBoundaryRef[] seaPortals = Array.Empty<MapBoundaryRef>(), grottoPortals = Array.Empty<MapBoundaryRef>();
        MapBoundaryChain floorEdge = Edge("grotto-west-floor", "grotto-floor", 8, 0, 8, 4);
        MapBoundaryChain roofEdge = Edge("grotto-west-roof", "grotto-roof", 8, 0, 8, 4);
        anchor.Records.Add(floorEdge);
        anchor.Records.Add(roofEdge);
        if (portalBottomY is not { } bottomY)
            AddWestStrip("grotto-west", floorEdge, roofEdge);
        else
        {
            MapBoundaryChain bottom = Authored("grotto-mouth-bottom", "grotto-floor", 8, 0, 8, 4, (int)MathF.Round(bottomY * 100));
            MapBoundaryChain top = Authored("grotto-mouth-top", "grotto-floor", 8, 0, 8, 4, 300);
            anchor.Records.Add(bottom);
            anchor.Records.Add(top);
            if (bottomY > -10f) AddWestStrip("grotto-riser", floorEdge, bottom);
            AddWestStrip("grotto-header", top, roofEdge);
            anchor.Records.Add(new MapCavePortal("grotto-mouth", new("grotto", anchor.Key), new("sea", Slot0("sea-floor")),
                Enumerable.Range(0, 5).Select(z => new MapLatticeVertex("grotto-floor", MapLatticeAddress.Corner(8, z))).ToArray(),
                new(bottom.Id, anchor.Key), new(top.Id, anchor.Key)));
            grottoPortals = new[] { new MapBoundaryRef(new("grotto-mouth", anchor.Key), MapSide.Front) };
            seaPortals = new[] { new MapBoundaryRef(new("grotto-mouth", anchor.Key), MapSide.Back) };
        }
        UpdateSpace(set, Slot0("grotto-floor"), "grotto", s => s with { Walls = s.Walls.Concat(grottoWalls).ToArray(), Portals = grottoPortals });
        UpdateSpace(set, Slot0("sea-floor"), "sea", s => s with { Walls = seaWalls.ToArray(), Portals = seaPortals });

        var water = new MapWaterSet();
        water.Media.Add(new MapWaterMediumDoc(Medium, MapWaterMediumKind.Water, 1f));
        water.Bodies.Add(new MapWaterBodyDoc("ocean", 0f, Medium, Material,
            new MapWaterDomainDoc(Frame, new[] { Rect(0, 0, 12, 4) }, Array.Empty<MapWaterRing>()),
            new[] { Cover("sea", "sea-floor"), Cover("grotto", "grotto-floor") }));
        return Document(set, water);

        void AddWestStrip(string id, MapBoundaryChain lower, MapBoundaryChain upper)
        {
            anchor.Records.Add(new MapWallStrip(id, new(lower.Id, anchor.Key), new(upper.Id, anchor.Key), MapStripFacing.TwoSided, 1));
            seaWalls.Add(new MapBoundaryRef(new(id, anchor.Key), MapSide.Front));
            grottoWalls.Add(new MapBoundaryRef(new(id, anchor.Key), MapSide.Back));
        }
    }

    internal static MapFramePoint GrottoPoint(float y) => Point(10, y, -2);

    /// <summary>A 2 by 2 cave on a lattice of 1 m cells and height unit 1/2,000,000,000 m, floor exactly -1 m and a
    /// flat roof one height unit above it, under body <c>hairline</c> at <c>SurfaceY</c> 0. A point at y -1 is
    /// exactly wet while both float bounds of its interval round to -1.</summary>
    internal static MapDocument HairlineCave()
    {
        var frame = new MapLatticeFrame(new(1, 1), new(1, 2_000_000_000), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0);
        var set = new MapSurfaceSet();
        Surface(set, "hair-floor", MapSurfaceRole.SupportFloor, frame, 0, 0, 2, 2, (_, _) => -2_000_000_000);
        Surface(set, "hair-roof", MapSurfaceRole.Ceiling, frame, 0, 0, 2, 2, (_, _) => -1_999_999_999);
        Footprints(set, "hairline-cave", MapSpaceKind.Cave, frame, 0, 0, 2, 2, Floor("hair-floor"), Ceiling("hair-roof"), Slot0("hair-floor"));
        var water = new MapWaterSet();
        water.Media.Add(new MapWaterMediumDoc(Medium, MapWaterMediumKind.Water, 1f));
        water.Bodies.Add(new MapWaterBodyDoc("hairline", 0f, Medium, Material,
            new MapWaterDomainDoc(frame, new[] { Rect(0, 0, 2, 2) }, Array.Empty<MapWaterRing>()),
            new[] { Cover("hairline-cave", "hair-floor") }));
        return Document(set, water);
    }

    static MapBoundaryChain Authored(string id, string surface, int x0, int z0, int x1, int z1, int heightUnits)
    {
        int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(z1 - z0));
        return new(id, MapChainKind.Authored, null, Enumerable.Range(0, steps + 1).Select(i => new MapChainVertex(new MapLatticeVertex(
            surface, MapLatticeAddress.Corner(x0 + Math.Sign(x1 - x0) * i, z0 + Math.Sign(z1 - z0) * i)), heightUnits)).ToArray());
    }

    static void UpdateSpace(MapSurfaceSet set, MapPatchKey anchor, string spaceId, Func<MapSpaceDoc, MapSpaceDoc> change)
    {
        var records = set.Patches[anchor].Records;
        int index = records.FindIndex(r => r.Id == spaceId);
        records[index] = change((MapSpaceDoc)records[index]);
    }
}
