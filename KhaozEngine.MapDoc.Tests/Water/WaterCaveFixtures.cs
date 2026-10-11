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
    internal static MapDocument SeaConnectedCave(float? portalBottomY) =>
        SeaConnectedCave(Frame, 100, portalBottomY is { } y ? (int)MathF.Round(y * 100) : null, 0f, Array.Empty<string>());

    /// <summary>Height units per metre of <see cref="FineSeaConnectedCave"/>, fine enough that a height one unit
    /// below 2 m rounds to 2 m in binary32.</summary>
    internal const int FineUnitsPerMetre = 1 << 26;

    /// <summary><see cref="SeaConnectedCave(float?)"/> on a lattice of 2^-26 m height units, with the portal bottom
    /// at <paramref name="portalBottomUnits"/> and the body at <paramref name="surfaceY"/>.</summary>
    internal static MapDocument FineSeaConnectedCave(int portalBottomUnits, float surfaceY) => SeaConnectedCave(
        new MapLatticeFrame(new(1, 1), new(1, FineUnitsPerMetre), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0),
        FineUnitsPerMetre, portalBottomUnits, surfaceY, Array.Empty<string>());

    /// <summary><see cref="SeaConnectedCave(float?)"/> with the portal bottom at -5 m, where each of
    /// <paramref name="owners"/> keeps only its bare record and its alias <c>{owner}-alias</c> holds its footprints,
    /// walls and portal list, so the portal names the alias as its endpoint.</summary>
    internal static MapDocument SeaConnectedCaveThroughAliases(params string[] owners) =>
        SeaConnectedCave(Frame, 100, -500, 0f, owners);

    /// <summary><see cref="SeaConnectedCave(float?)"/> at a -5 m portal bottom whose bottom chain is an authored chain
    /// with one vertex missing its height, so the portal aperture is invalid. Unvalidated.</summary>
    internal static MapDocument SeaConnectedCaveWithInvalidPortalChain()
    {
        MapDocument doc = SeaConnectedCave(-5f);
        List<MapTopologyRecord> records = doc.Surfaces.Patches[Slot0("grotto-floor")].Records;
        int at = records.FindIndex(r => r.Id == "grotto-mouth-bottom");
        var chain = (MapBoundaryChain)records[at];
        records[at] = chain with { Vertices = chain.Vertices.Select((v, i) => i == 0 ? v with { HeightUnits = null } : v).ToArray() };
        return doc;
    }

    /// <summary>The record refs of the portal and both spaces of <see cref="SeaConnectedCave(float?)"/>.</summary>
    internal static (MapRecordRef Portal, MapRecordRef Grotto, MapRecordRef Sea) SeaCaveRefs(MapDocument doc) =>
        (RecordRef(doc, "grotto-mouth"), RecordRef(doc, "grotto"), RecordRef(doc, "sea"));

    static MapDocument SeaConnectedCave(MapLatticeFrame frame, int unitsPerMetre, int? portalBottomUnits, float surfaceY,
        IReadOnlyCollection<string> aliasedEndpoints)
    {
        var set = new MapSurfaceSet();
        Surface(set, "sea-floor", MapSurfaceRole.SupportFloor, frame, 0, 0, 8, 4, (_, _) => -10 * unitsPerMetre);
        Surface(set, "grotto-floor", MapSurfaceRole.SupportFloor, frame, 8, 0, 12, 4, (_, _) => -10 * unitsPerMetre);
        Surface(set, "grotto-roof", MapSurfaceRole.Ceiling, frame, 8, 0, 12, 4, (_, _) => 5 * unitsPerMetre);
        Surface(set, "cliff-top", MapSurfaceRole.SupportFloor, frame, 8, 0, 12, 4, (_, _) => 5 * unitsPerMetre);

        Footprints(set, "sea", MapSpaceKind.Exterior, frame, 0, 0, 8, 4, Floor("sea-floor"), OpenTop, Slot0("sea-floor"));
        FootprintCells(set, "sea", "sea-cliff", 8, 0, 12, 4, Floor("cliff-top"), OpenTop, Slot0("sea-floor"));
        Footprints(set, "grotto", MapSpaceKind.Cave, frame, 8, 0, 12, 4, Floor("grotto-floor"), Ceiling("grotto-roof"), Slot0("grotto-floor"));
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
        if (portalBottomUnits is not { } bottomUnits)
            AddWestStrip("grotto-west", floorEdge, roofEdge);
        else
        {
            MapBoundaryChain bottom = Authored("grotto-mouth-bottom", "grotto-floor", 8, 0, 8, 4, bottomUnits);
            MapBoundaryChain top = Authored("grotto-mouth-top", "grotto-floor", 8, 0, 8, 4, 3 * unitsPerMetre);
            anchor.Records.Add(bottom);
            anchor.Records.Add(top);
            if (bottomUnits > -10 * unitsPerMetre) AddWestStrip("grotto-riser", floorEdge, bottom);
            AddWestStrip("grotto-header", top, roofEdge);
            anchor.Records.Add(new MapCavePortal("grotto-mouth", new("grotto", anchor.Key), new("sea", Slot0("sea-floor")),
                Enumerable.Range(0, 5).Select(z => new MapLatticeVertex("grotto-floor", MapLatticeAddress.Corner(8, z))).ToArray(),
                new(bottom.Id, anchor.Key), new(top.Id, anchor.Key)));
            grottoPortals = new[] { new MapBoundaryRef(new("grotto-mouth", anchor.Key), MapSide.Front) };
            seaPortals = new[] { new MapBoundaryRef(new("grotto-mouth", anchor.Key), MapSide.Back) };
        }
        UpdateSpace(set, Slot0("grotto-floor"), "grotto", s => s with { Walls = s.Walls.Concat(grottoWalls).ToArray(), Portals = grottoPortals });
        UpdateSpace(set, Slot0("sea-floor"), "sea", s => s with { Walls = seaWalls.ToArray(), Portals = seaPortals });
        foreach (string owner in aliasedEndpoints) MoveToAlias(set, owner);

        var water = new MapWaterSet();
        water.Media.Add(new MapWaterMediumDoc(Medium, MapWaterMediumKind.Water, 1f));
        water.Bodies.Add(new MapWaterBodyDoc("ocean", surfaceY, Medium, Material,
            new MapWaterDomainDoc(frame, new[] { Rect(0, 0, 12, 4) }, Array.Empty<MapWaterRing>()),
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

    /// <summary>Adds <c>{owner}-alias</c> beside the space <paramref name="ownerId"/> and hands it the owner's
    /// footprints, walls, portals and portal endpoints, since R2 closes each footprint column with its own space's
    /// boundary list and lists a portal only on the record it names.</summary>
    static void MoveToAlias(MapSurfaceSet set, string ownerId)
    {
        MapSurfacePatch anchor = set.Patches.Values.Single(p => p.Records.Any(r => r.Id == ownerId));
        var owner = new MapRecordRef(ownerId, anchor.Key);
        var alias = new MapRecordRef(ownerId + "-alias", anchor.Key);
        int at = anchor.Records.FindIndex(r => r.Id == ownerId);
        var space = (MapSpaceDoc)anchor.Records[at];
        anchor.Records[at] = space with { Walls = Array.Empty<MapBoundaryRef>(), Portals = Array.Empty<MapBoundaryRef>() };
        anchor.Records.Add(space with { Id = alias.Id, AliasOf = owner });
        foreach (MapSurfacePatch patch in set.Patches.Values)
            for (int i = 0; i < patch.Records.Count; i++)
                patch.Records[i] = patch.Records[i] switch
                {
                    MapSpaceFootprint footprint when footprint.Space == owner => footprint with { Space = alias },
                    MapCavePortal portal when portal.FromSpace == owner => portal with { FromSpace = alias },
                    MapCavePortal portal when portal.ToSpace == owner => portal with { ToSpace = alias },
                    var record => record,
                };
    }

    static void UpdateSpace(MapSurfaceSet set, MapPatchKey anchor, string spaceId, Func<MapSpaceDoc, MapSpaceDoc> change)
    {
        var records = set.Patches[anchor].Records;
        int index = records.FindIndex(r => r.Id == spaceId);
        records[index] = change((MapSpaceDoc)records[index]);
    }
}
