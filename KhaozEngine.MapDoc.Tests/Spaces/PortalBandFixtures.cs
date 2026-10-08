using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc;

internal static class PortalBandFixtures
{
    internal static MapDocument Build(string variant)
    {
        if (variant is not ("header" or "header-missing" or "header-overlap" or "riser" or "lintel" or
            "arch" or "arch-mismatch" or "outside-air" or "duplicate-side" or "cave-open-edge"))
            throw new ArgumentException("unknown portal band fixture variant", nameof(variant));
        int passageFloor = variant == "riser" ? 100 : 0;
        int hallCeiling = variant == "lintel" ? 500 : 3000;
        int passageCeiling = variant switch { "riser" => 400, "lintel" => 600, "arch" or "arch-mismatch" => 500, _ => 300 };
        var set = new MapSurfaceSet();
        CaveFixtures.AddSurface(set, "hall-floor", MapSurfaceRole.SupportFloor, 0, 0, 2, 2, (_, _) => 0);
        CaveFixtures.AddSurface(set, "hall-ceiling", MapSurfaceRole.Ceiling, 0, 0, 2, 2, (_, _) => hallCeiling);
        CaveFixtures.AddSurface(set, "passage-floor", MapSurfaceRole.SupportFloor, 2, 0, 2, 2, (_, _) => passageFloor);
        CaveFixtures.AddSurface(set, "passage-ceiling", MapSurfaceRole.Ceiling, 2, 0, 2, 2, (_, _) => passageCeiling);
        MapSurfacePatch anchor = CaveFixtures.Patch(set, "hall-floor");
        var hallWalls = new List<MapBoundaryRef>();
        var passageWalls = new List<MapBoundaryRef>();
        Close("hall", "hall-floor", "hall-ceiling", 0, 0, 0, 2, MapStripFacing.Front, hallWalls);
        Close("hall-south", "hall-floor", "hall-ceiling", 0, 0, 2, 0, MapStripFacing.Back, hallWalls);
        Close("hall-north", "hall-floor", "hall-ceiling", 0, 2, 2, 2, MapStripFacing.Front, hallWalls);
        if (variant != "cave-open-edge")
            Close("passage-east", "passage-floor", "passage-ceiling", 4, 0, 4, 2, MapStripFacing.Back, passageWalls);
        Close("passage-south", "passage-floor", "passage-ceiling", 2, 0, 4, 0, MapStripFacing.Back, passageWalls);
        Close("passage-north", "passage-floor", "passage-ceiling", 2, 2, 4, 2, MapStripFacing.Front, passageWalls);

        MapBoundaryChain bottom = Edge("door-bottom", variant == "riser" ? "passage-floor" : "hall-floor");
        MapBoundaryChain top = variant switch
        {
            "lintel" => Authored("lintel-bottom", new[] { 300, 300, 300 }),
            "arch" or "arch-mismatch" => Authored("arch", new[] { 300, 400, 300 }),
            "outside-air" => Authored("door-top", new[] { 3100, 3100, 3100 }),
            _ => Edge("door-top", "passage-ceiling"),
        };
        anchor.Records.Add(bottom);
        anchor.Records.Add(top);
        anchor.Records.Add(new MapCavePortal("door", Ref("hall"), Ref("passage"),
            CaveFixtures.Vertices("hall-floor", 2, 0, 2, 2), Ref(bottom.Id), Ref(top.Id)));

        if (variant == "lintel")
        {
            MapBoundaryChain lintelTop = Edge("lintel-top", "hall-ceiling");
            AddStrip("lintel", top, lintelTop, MapStripFacing.TwoSided);
            hallWalls.Add(Side("lintel", MapSide.Back));
            passageWalls.Add(Side("lintel", MapSide.Front));
            AddStrip("lintel-upper", lintelTop, Edge("lintel-upper-top", "passage-ceiling"), MapStripFacing.Front);
            passageWalls.Add(Side("lintel-upper", MapSide.Front));
        }
        else if (variant != "header-missing")
        {
            MapBoundaryChain lower = variant switch
            {
                "header-overlap" => Authored("header-bottom", new[] { 250, 250, 250 }),
                "arch" => top,
                "arch-mismatch" => new("header-bottom", MapChainKind.Authored, null, new[]
                {
                    new MapChainVertex(CaveFixtures.Vertex("hall-floor", 2, 0), 300),
                    new MapChainVertex(CaveFixtures.Vertex("hall-floor", 2, 2), 300),
                }),
                _ => Edge("header-bottom", "passage-ceiling"),
            };
            AddStrip("header", lower, Edge("header-top", "hall-ceiling"), MapStripFacing.Back);
            hallWalls.Add(Side("header", MapSide.Back));
            if (variant == "duplicate-side") passageWalls.Add(Side("header", MapSide.Back));
        }
        if (variant is "arch" or "arch-mismatch")
        {
            AddStrip("arch-upper", top, Edge("arch-upper-top", "passage-ceiling"), MapStripFacing.Front);
            passageWalls.Add(Side("arch-upper", MapSide.Front));
        }
        if (variant == "riser")
        {
            AddStrip("riser", Edge("riser-bottom", "hall-floor"), bottom, MapStripFacing.Back);
            hallWalls.Add(Side("riser", MapSide.Back));
        }
        anchor.Records.Add(new MapSpaceDoc("hall", MapSpaceKind.Cave, null, null, Array.Empty<string>(), hallWalls.ToArray(),
            new[] { Side("door", MapSide.Front) }, Array.Empty<MapRecordRef>()));
        anchor.Records.Add(new MapSpaceDoc("passage", MapSpaceKind.Cave, null, null, Array.Empty<string>(), passageWalls.ToArray(),
            new[] { Side("door", MapSide.Back) }, Array.Empty<MapRecordRef>()));
        anchor.Records.Add(new MapSpaceFootprint("hall-cells", Ref("hall"), anchor.Key, CaveFixtures.Cells(0, 0, 2, 2),
            CaveFixtures.Floor("hall-floor"), CaveFixtures.Ceiling("hall-ceiling")));
        anchor.Records.Add(new MapSpaceFootprint("passage-cells", Ref("passage"), CaveFixtures.Key("passage-floor"), CaveFixtures.Cells(2, 0, 4, 2),
            CaveFixtures.Floor("passage-floor"), CaveFixtures.Ceiling("passage-ceiling")));
        return CaveFixtures.Document(set,
            variant == "duplicate-side" ? new[] { "duplicate boundary side 'header'" } : null,
            variant == "arch-mismatch" ? "header" : null);

        void Close(string id, string lowerSurface, string upperSurface,
            int x0, int z0, int x1, int z1, MapStripFacing facing, List<MapBoundaryRef> walls)
        {
            AddStrip(id, CaveFixtures.Edge(id + "-bottom", lowerSurface, x0, z0, x1, z1),
                CaveFixtures.Edge(id + "-top", upperSurface, x0, z0, x1, z1), facing);
            walls.Add(Side(id, facing == MapStripFacing.Front ? MapSide.Front : MapSide.Back));
        }

        void AddStrip(string id, MapBoundaryChain lower, MapBoundaryChain upper, MapStripFacing facing)
        {
            if (!anchor.Records.Any(r => r.Id == lower.Id)) anchor.Records.Add(lower);
            if (!anchor.Records.Any(r => r.Id == upper.Id)) anchor.Records.Add(upper);
            anchor.Records.Add(new MapWallStrip(id, Ref(lower.Id), Ref(upper.Id), facing, 1));
        }
    }

    internal static IReadOnlyList<MapCompiledStrip> CompileAllStrips(MapDocument doc)
    {
        MapScopedSurfaces view = CaveFixtures.View(doc);
        return doc.Surfaces.AllRecords().OfType<MapWallStrip>().OrderBy(s => s.Id, StringComparer.Ordinal)
            .Select(strip => MapWallStripCompiler.Compile(strip,
                MapBoundaryGeometry.ResolveChain(CaveFixtures.Record<MapBoundaryChain>(doc, strip.LowerChain.Id), view),
                MapBoundaryGeometry.ResolveChain(CaveFixtures.Record<MapBoundaryChain>(doc, strip.UpperChain.Id), view))).ToArray();
    }

    static MapBoundaryChain Edge(string id, string surface) => CaveFixtures.Edge(id, surface, 2, 0, 2, 2);
    static MapBoundaryChain Authored(string id, IReadOnlyList<int> heights) => new(id, MapChainKind.Authored, null,
        heights.Select((height, z) => new MapChainVertex(CaveFixtures.Vertex("hall-floor", 2, z), height)).ToArray());
    static MapRecordRef Ref(string id) => new(id, CaveFixtures.Key("hall-floor"));
    static MapBoundaryRef Side(string id, MapSide side) => new(Ref(id), side);
}
