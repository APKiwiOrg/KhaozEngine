using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SpaceCoverageRegressionTests
{
    [Fact]
    public void SharedPortalEndpoint_UsesBothSegmentVerticesAndLowestRecordOwner()
    {
        MapDocument document = SharedPortals(subdivided: false);
        MapMembershipResult result = CaveFixtures.Query(document, 1, 1, 1);

        // a-door ends at row 1. Its row-0 source vertex must survive the row-1 point query.
        Assert.Equal((MapMembershipStatus.Resolved, "left"), (result.Status, result.SpaceId));
        Assert.Equal("left", CaveFixtures.Query(document, 1, 1, 0.5f).SpaceId);
        Assert.Equal("right", CaveFixtures.Query(document, 1, 1, 1.5f).SpaceId);
    }

    [Fact]
    public void PortalEndpoint_DemandsTheCellThatEmitsItsSubdivisionVertex()
    {
        MapDocument document = SharedPortals(subdivided: true);
        MapMembershipResult result = CaveFixtures.Query(document, 1, 1, 1);

        // Only the source patch's preceding row emits the midpoint at (1, 1/2).
        Assert.Equal((MapMembershipStatus.Resolved, "left"), (result.Status, result.SpaceId));
    }

    [Fact]
    public void PortalSourceCells_AreChargedBeforeCompilation()
    {
        MapMembershipResult result = CaveFixtures.Query(SharedPortals(subdivided: false), 1, 1, 1,
            new MapQueryLimits(MaxInspectedFaces: 3));

        // The preceding source row and the point row alone require at least four faces.
        Assert.Equal(MapMembershipStatus.CapacityExceeded, result.Status);
    }

    [Theory]
    [InlineData(MapStripFacing.Front, MapSide.Back, false)]
    [InlineData(MapStripFacing.Back, MapSide.Front, false)]
    [InlineData(MapStripFacing.Back, MapSide.Back, true)]
    [InlineData(MapStripFacing.TwoSided, MapSide.Back, true)]
    [InlineData(MapStripFacing.TwoSided, MapSide.Front, true)]
    public void HeaderCoverage_RequiresTheRegisteredSideToBeEmitted(MapStripFacing facing, MapSide side, bool covered)
    {
        MapSurfaceSet set = PortalBandFixtures.Build("header").Surfaces.Clone();
        MapSurfacePatch anchor = CaveFixtures.Patch(set, "hall-floor");
        MapWallStrip header = set.AllRecords().OfType<MapWallStrip>().Single(r => r.Id == "header");
        Replace(anchor, header with { Facing = facing });
        MapSpaceDoc hall = set.AllRecords().OfType<MapSpaceDoc>().Single(r => r.Id == "hall");
        Replace(anchor, hall with
        {
            Walls = hall.Walls.Select(w => w.Record.Id == "header" ? w with { Side = side } : w).ToArray(),
        });
        Assert.Empty(MapTopologyReferenceValidator.Validate(set.Refs, set.Patches.Values));

        var findings = MapSpaceCoverageValidator.Validate(MapScopedSurfaces.CompleteView(set));

        if (covered) Assert.Empty(findings);
        else Assert.Equal(new[] { "gap: footprint 'hall-cells' cell 1", "gap: footprint 'hall-cells' cell 65" }, findings);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void CompatibilityOnlyPeers_ReportAmbiguityWithoutPhysicalGeometry(int cell, bool aliased)
    {
        MapSurfaceSet set = LegacyExteriorFixtures.Row("tagged").Surfaces.Clone();
        MapSurfacePatch anchor = CaveFixtures.Patch(set, "plane-0");
        MapSpaceFootprint footprint = set.AllRecords().OfType<MapSpaceFootprint>().Single();
        Replace(anchor, footprint with { SlotCells = new[] { cell } });
        var world = new MapRecordRef("world", anchor.Key);
        anchor.Records.Add(new MapSpaceDoc("world-copy", MapSpaceKind.Exterior, null, aliased ? world : null,
            Array.Empty<string>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>()));
        anchor.Records.Add(footprint with { Id = "world-copy-cells", Space = new("world-copy", anchor.Key), SlotCells = new[] { cell } });
        MapDocument document = CaveFixtures.Document(set);
        MapScopedSurfaces view = CaveFixtures.View(document);
        foreach (MapSpaceFootprint peer in set.AllRecords().OfType<MapSpaceFootprint>())
        {
            MapCellRefinement refinement = MapCommonRefinement.RefineFootprintCell(view, peer, cell, new());
            Assert.Equal(new MapExactValue(1, 1), refinement.CompatibilityArea);
            Assert.Empty(refinement.Faces);
            Assert.Empty(refinement.Vertices);
            Assert.Null(refinement.MinSeparation);
        }

        var findings = MapSpaceCoverageValidator.Validate(view);

        if (aliased) Assert.Empty(findings);
        else Assert.Equal(new[] { $"ambiguous: footprint 'world-cells' cell {cell} with 'world-copy-cells' cell {cell}" }, findings);
        Assert.Equal(aliased ? MapMembershipStatus.Resolved : MapMembershipStatus.Ambiguous,
            CaveFixtures.Query(document, cell + 0.5f, 10, -0.5f).Status);
    }

    [Theory]
    [InlineData(-100, 100, 0, false)]
    [InlineData(100, -100, 0, false)]
    [InlineData(-100, 100, 100, true)]
    public void PortalBottom_IsContainedAcrossTheWholeLinearInterval(int first, int last, int bottom, bool contained)
    {
        MapDocument document = AdjacentColumns(new[] { first, last }, bottom);

        var findings = MapSpaceCoverageValidator.Validate(CaveFixtures.View(document));

        if (contained) Assert.Empty(findings);
        else Assert.Equal(AirFindings, findings);
    }

    [Fact]
    public void PortalBottom_ChecksThePeakBetweenLinearPieces()
    {
        MapDocument document = AdjacentColumns(new[] { -100, 100, -100 }, 0);

        Assert.Equal(AirFindings, MapSpaceCoverageValidator.Validate(CaveFixtures.View(document)));
    }

    [Fact]
    public void PortalTop_IsContainedAcrossTheWholeLinearInterval()
    {
        MapDocument document = AdjacentColumns(new[] { -100, -100 }, 0, new[] { 100, 300 }, 200);

        Assert.Equal(AirFindings, MapSpaceCoverageValidator.Validate(CaveFixtures.View(document)));
    }

    static readonly string[] AirFindings =
    {
        "air interval: portal 'door' side 'left'",
        "air interval: portal 'door' side 'right'",
    };

    static MapDocument SharedPortals(bool subdivided)
    {
        var set = new MapSurfaceSet();
        CaveFixtures.AddSurface(set, "floor", MapSurfaceRole.SupportFloor, 0, 0, 2, 2, (_, _) => 0);
        MapSurfacePatch anchor = CaveFixtures.Patch(set, "floor");
        string source = subdivided ? "portal-source" : "floor";
        if (subdivided)
        {
            CaveFixtures.AddSurface(set, source, MapSurfaceRole.SupportFloor, 0, 0, 1, 2, (_, _) => 0);
            CaveFixtures.Patch(set, source).EdgeSubdivisions.Add(new(0, 0, MapCellEdge.East, 2));
        }
        MapRecordRef Ref(string id) => new(id, anchor.Key);
        MapBoundaryChain first = CaveFixtures.Edge("a-bottom", source, 1, 0, 1, 1);
        if (subdivided) first = first with
        {
            Vertices = new[]
            {
                new MapChainVertex(CaveFixtures.Vertex(source, 1, 0), null),
                new MapChainVertex(new(source, MapLatticeAddress.Create(2, 1, 2)), null),
                new MapChainVertex(CaveFixtures.Vertex(source, 1, 1), null),
            },
        };
        anchor.Records.Add(first);
        anchor.Records.Add(CaveFixtures.Edge("z-bottom", "floor", 1, 1, 1, 2));
        anchor.Records.Add(new MapCavePortal("a-door", Ref("right"), Ref("left"), first.Vertices.Select(v => v.Vertex).ToArray(), Ref(first.Id), null));
        anchor.Records.Add(new MapCavePortal("z-door", Ref("left"), Ref("right"), CaveFixtures.Vertices("floor", 1, 1, 1, 2), Ref("z-bottom"), null));
        AddSpace("left", 0, new[] { new MapBoundaryRef(Ref("a-door"), MapSide.Back), new(Ref("z-door"), MapSide.Front) });
        AddSpace("right", 1, new[] { new MapBoundaryRef(Ref("a-door"), MapSide.Front), new(Ref("z-door"), MapSide.Back) });
        return CaveFixtures.Document(set);

        void AddSpace(string id, int x, MapBoundaryRef[] portals)
        {
            anchor.Records.Add(new MapSpaceDoc(id, MapSpaceKind.Exterior, null, null, Array.Empty<string>(),
                Array.Empty<MapBoundaryRef>(), portals, Array.Empty<MapRecordRef>()));
            anchor.Records.Add(new MapSpaceFootprint(id + "-cells", Ref(id), anchor.Key, new[] { x, 64 + x },
                CaveFixtures.Floor("floor"), CaveFixtures.OpenTop));
        }
    }

    static MapDocument AdjacentColumns(int[] floor, int bottom, int[]? ceiling = null, int? top = null)
    {
        var set = new MapSurfaceSet();
        int depth = floor.Length - 1;
        CaveFixtures.AddSurface(set, "floor", MapSurfaceRole.SupportFloor, 0, 0, 2, depth, (_, z) => floor[z]);
        if (ceiling is not null)
            CaveFixtures.AddSurface(set, "ceiling", MapSurfaceRole.Ceiling, 0, 0, 2, depth, (_, z) => ceiling[z]);
        MapSurfacePatch anchor = CaveFixtures.Patch(set, "floor");
        MapRecordRef Ref(string id) => new(id, anchor.Key);
        MapLatticeVertex[] vertices = CaveFixtures.Vertices("floor", 1, 0, 1, depth);
        anchor.Records.Add(new MapBoundaryChain("bottom", MapChainKind.Authored, null, vertices.Select(v => new MapChainVertex(v, bottom)).ToArray()));
        if (top is { } height)
            anchor.Records.Add(new MapBoundaryChain("top", MapChainKind.Authored, null, vertices.Select(v => new MapChainVertex(v, height)).ToArray()));
        anchor.Records.Add(new MapCavePortal("door", Ref("left"), Ref("right"), vertices, Ref("bottom"), top is null ? null : Ref("top")));
        AddSpace("left", 0, MapSide.Front, null);
        AddSpace("right", 1, MapSide.Back, Ref("left"));
        return CaveFixtures.Document(set);

        void AddSpace(string id, int x, MapSide side, MapRecordRef? alias)
        {
            anchor.Records.Add(new MapSpaceDoc(id, MapSpaceKind.Exterior, null, alias, Array.Empty<string>(),
                Array.Empty<MapBoundaryRef>(), new[] { new MapBoundaryRef(Ref("door"), side) }, Array.Empty<MapRecordRef>()));
            anchor.Records.Add(new MapSpaceFootprint(id + "-cells", Ref(id), anchor.Key,
                Enumerable.Range(0, depth).Select(z => z * 64 + x).ToArray(), CaveFixtures.Floor("floor"),
                ceiling is null ? CaveFixtures.OpenTop : CaveFixtures.Ceiling("ceiling")));
        }
    }

    static void Replace(MapSurfacePatch patch, MapTopologyRecord replacement)
        => patch.Records[patch.Records.FindIndex(r => r.Id == replacement.Id)] = replacement;
}
