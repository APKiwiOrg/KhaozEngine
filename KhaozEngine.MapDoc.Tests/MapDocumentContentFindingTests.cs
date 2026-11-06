using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapDocumentContentFindingTests
{
    [Theory]
    [InlineData("region", MapDocumentRefusal.SchemaValueRequired, "$.regions[1].shape")]
    [InlineData("feature-duplicate", MapDocumentRefusal.IdentityDuplicate, "$.terrain.features[1].name")]
    [InlineData("placement-id", MapDocumentRefusal.SchemaValueRequired, "$.placements[1].id")]
    [InlineData("placement-duplicate", MapDocumentRefusal.IdentityDuplicate, "$.placements[1].id")]
    [InlineData("placement-kind", MapDocumentRefusal.SchemaValueRequired, "$.placements[1].kind")]
    [InlineData("placement-scale", MapDocumentRefusal.ValueOutsideRange, "$.placements[1].scale")]
    [InlineData("placement-nonfinite", MapDocumentRefusal.ValueNonfinite, "$.placements[1].scale")]
    [InlineData("spawn-id", MapDocumentRefusal.SchemaValueRequired, "$.spawns[1].id")]
    [InlineData("spawn-duplicate", MapDocumentRefusal.IdentityDuplicate, "$.spawns[1].id")]
    [InlineData("spawn-archetype", MapDocumentRefusal.SchemaValueRequired, "$.spawns[1].archetypeId")]
    [InlineData("player-id", MapDocumentRefusal.SchemaValueRequired, "$.playerSpawns[1].id")]
    [InlineData("player-duplicate", MapDocumentRefusal.IdentityDuplicate, "$.playerSpawns[1].id")]
    [InlineData("exclusion-layer", MapDocumentRefusal.ReferenceMissing, "$.exclusions[1].layers[0]")]
    [InlineData("override-layer", MapDocumentRefusal.ReferenceMissing, "$.scatterOverrides[1].layers[0]")]
    [InlineData("paint", MapDocumentRefusal.SurfacePaintPriorityMisplaced, "$.surfaces.refs[0].paintPriority")]
    public void ContentFinding_PreservesTheFailedFieldAndItsRepairedControl(string fault, MapDocumentRefusal reason, string path)
    {
        var doc = MapDocumentFindingTests.ValidDocument();
        foreach (string id in new[] { "first", "second" })
        {
            doc.Placements.Add(new() { Id = id, Kind = "prop" });
            doc.Spawns.Add(new() { Id = id, ArchetypeId = "actor" });
            doc.PlayerSpawns.Add(new() { Id = id });
            doc.Terrain.Features.Add(new LakeFeatureDoc { Name = id });
        }
        if (fault == "paint")
        {
            doc.ResolverIdentity = new(1, 2);
            doc.SupportRecipe = MapSupportRecipe.AuthoredBindingsV2;
            doc.Surfaces.Refs.Add(MixedResolutionFixtures.Surface("floor", 1, MapSurfaceRole.SupportFloor) with { SemanticSha256 = new string('a', 64) });
        }
        Action repair = Break(doc, fault);
        var findings = new List<MapDocumentFinding>();
        var errors = MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault(), findings);
        var finding = Assert.Single(findings);
        Assert.Single(errors);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(path, finding.FieldPath);
        Assert.Equal(fault == "paint" ? 0 : 1, finding.Index);
        Assert.Equal(errors, findings.Select(f => f.Message));
        if (fault is "exclusion-layer" or "override-layer")
        {
            Assert.Equal("ghost", finding.LayerId);
            Assert.Equal("second", finding.EntityId);
        }
        else if (fault == "paint")
        {
            Assert.Equal("floor", finding.SurfaceId);
            Assert.Equal(1L, finding.ActualInteger);
        }
        else Assert.Equal(fault.EndsWith("-id", StringComparison.Ordinal) ? "" : fault.EndsWith("duplicate", StringComparison.Ordinal) ? "first" : "second", finding.EntityId);
        if (fault == "placement-scale")
        {
            Assert.Equal(0d, finding.ActualNumber);
            Assert.Equal(0d, finding.MinimumNumber);
        }
        if (fault == "placement-nonfinite") Assert.Null(finding.ActualNumber);
        if (fault is "placement-id" or "placement-kind" or "placement-scale" or "placement-nonfinite" or "spawn-id" or "spawn-archetype" or "player-id")
        {
            var sliceFindings = new List<MapDocumentFinding>();
            var content = new MapTileContent(new(0, 0), doc.Placements, doc.Spawns, doc.PlayerSpawns, []);
            Assert.Equal(errors, MapDocumentValidator.ValidateContent(doc, new Dictionary<string, MapSurfaceRef>(), content, [], sliceFindings));
            Assert.Equal(findings, sliceFindings);
        }
        repair();
        findings.Clear();
        Assert.Empty(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault(), findings));
        Assert.Empty(findings);
    }

    [Fact]
    public void UnregisteredFeature_RetainsTheDiscriminantReadByTheRegistryCheck()
    {
        var doc = MapDocumentFindingTests.ValidDocument();
        doc.Terrain.Features.Add(new ChangingFeature { Name = "feature" });
        var findings = new List<MapDocumentFinding>();
        Assert.Single(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault(), findings));
        var finding = Assert.Single(findings);
        Assert.Equal(MapDocumentRefusal.DocumentTerrainFeatureUnknown, finding.Reason);
        Assert.Equal("missing", finding.ActualText);
        Assert.Equal("feature", finding.EntityId);
        Assert.Equal("$.terrain.features[0].type", finding.FieldPath);
        Assert.Equal(0, finding.Index);
        doc.Terrain.Features[0] = new LakeFeatureDoc { Name = "feature" };
        Assert.Empty(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault()));
    }

    [Theory]
    [InlineData(0f, MapDocumentRefusal.ValueOutsideRange)]
    [InlineData(float.NaN, MapDocumentRefusal.ValueNonfinite)]
    [InlineData(float.PositiveInfinity, MapDocumentRefusal.ValueNonfinite)]
    [InlineData(8f, MapDocumentRefusal.BoundsInvalid)]
    public void TileSizeFinding_RetainsItsDomainAndAcceptsOneSculptSpan(float size, MapDocumentRefusal reason)
    {
        var doc = MapDocumentFindingTests.ValidDocument();
        doc.TileSize = size;
        var findings = new List<MapDocumentFinding>();
        Assert.Single(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault(), findings));
        var finding = Assert.Single(findings);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal("$.tileSize", finding.FieldPath);
        Assert.Equal(float.IsFinite(size) ? size : (double?)null, finding.ActualNumber);
        Assert.Equal(size == 8f ? 16d : 0d, finding.MinimumNumber);
        doc.TileSize = 16;
        Assert.Empty(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault()));
    }

    [Theory]
    [InlineData(-1f, MapDocumentRefusal.ValueOutsideRange)]
    [InlineData(float.NaN, MapDocumentRefusal.ValueNonfinite)]
    public void InvalidOverrideCellSize_UsesTheProductionGuardWithoutMutatingAConstructedLayer(float cellSize, MapDocumentRefusal reason)
    {
        var finding = Assert.IsType<MapDocumentFinding>(MapDocumentValidator.InvalidOverrideCellSizeForTests(cellSize));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal("$.terrainOverrides.cellSize", finding.FieldPath);
        Assert.Equal(float.IsFinite(cellSize) ? cellSize : (double?)null, finding.ActualNumber);
        Assert.Equal(0d, finding.MinimumNumber);
        Assert.Null(MapDocumentValidator.InvalidOverrideCellSizeForTests(1));
    }

    [Theory]
    [InlineData(-32, 0, "minX", -1, 0, -16d)]
    [InlineData(224, 0, "maxX", 7, 0, 127.5d)]
    [InlineData(0, -32, "minZ", 0, -1, -16d)]
    [InlineData(0, 224, "maxZ", 0, 7, 127.5d)]
    public void SculptExtentFinding_IdentifiesTheFirstEscapingAxisAndTile(int x, int z, string axis, int tileX, int tileZ, double actual)
    {
        var doc = MapDocumentFindingTests.ValidDocument();
        doc.Bounds.MinX = doc.Bounds.MinZ = 0;
        doc.TerrainOverrides = new();
        doc.TerrainOverrides.SetDelta(x, z, 1);
        var findings = new List<MapDocumentFinding>();
        Assert.Single(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault(), findings));
        var finding = Assert.Single(findings);
        Assert.Equal(MapDocumentRefusal.BoundsInvalid, finding.Reason);
        Assert.Equal("$.terrainOverrides.tiles[0]." + axis, finding.FieldPath);
        Assert.Equal(new MapTileCoord(tileX, tileZ), finding.SculptTile);
        Assert.Equal(actual, finding.ActualNumber);
        Assert.Equal(0d, finding.MinimumNumber);
        Assert.Equal(100d, finding.MaximumNumber);
        if (axis == "minX") doc.Bounds.MinX = (float)actual;
        else if (axis == "minZ") doc.Bounds.MinZ = (float)actual;
        else if (axis == "maxX") doc.Bounds.MaxX = (float)actual;
        else doc.Bounds.MaxZ = (float)actual;
        Assert.Empty(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault()));
    }

    [Fact]
    public void SculptExtent_NonfiniteArithmeticRetainsTheFirstEscapingAxis()
    {
        var doc = MapDocumentFindingTests.ValidDocument();
        doc.TerrainOverrides = new(float.MaxValue);
        var content = new MapTileContent(new(0, 0), [], [], [], [new(1, 0)]);
        var findings = new List<MapDocumentFinding>();
        // Tile (1, 0) starts at 32 * float.MaxValue. Both X endpoints overflow exactly to infinity.
        Assert.Single(MapDocumentValidator.ValidateContent(doc, new Dictionary<string, MapSurfaceRef>(), content, [], findings));
        var finding = Assert.Single(findings);
        Assert.Equal(MapDocumentRefusal.ValueNonfinite, finding.Reason);
        Assert.Equal("$.terrainOverrides.tiles[0].maxX", finding.FieldPath);
        Assert.Equal(new MapTileCoord(1, 0), finding.SculptTile);
        Assert.Null(finding.ActualNumber);
        doc.TerrainOverrides = new(0.5f);
        Assert.Empty(MapDocumentValidator.ValidateContent(doc, new Dictionary<string, MapSurfaceRef>(), content, []));
    }

    static Action Break(MapDocument doc, string fault)
    {
        switch (fault)
        {
            case "region": doc.Regions[1].Shape = null; return () => doc.Regions[1].Shape = new DiscShapeDoc();
            case "feature-duplicate": doc.Terrain.Features[1].Name = "first"; return () => doc.Terrain.Features[1].Name = "second";
            case "placement-id": doc.Placements[1].Id = ""; return () => doc.Placements[1].Id = "second";
            case "placement-duplicate": doc.Placements[1].Id = "first"; return () => doc.Placements[1].Id = "second";
            case "placement-kind": doc.Placements[1].Kind = ""; return () => doc.Placements[1].Kind = "prop";
            case "placement-scale": doc.Placements[1].Scale = 0; return () => doc.Placements[1].Scale = 1;
            case "placement-nonfinite": doc.Placements[1].Scale = float.NegativeInfinity; return () => doc.Placements[1].Scale = 1;
            case "spawn-id": doc.Spawns[1].Id = ""; return () => doc.Spawns[1].Id = "second";
            case "spawn-duplicate": doc.Spawns[1].Id = "first"; return () => doc.Spawns[1].Id = "second";
            case "spawn-archetype": doc.Spawns[1].ArchetypeId = ""; return () => doc.Spawns[1].ArchetypeId = "actor";
            case "player-id": doc.PlayerSpawns[1].Id = ""; return () => doc.PlayerSpawns[1].Id = "second";
            case "player-duplicate": doc.PlayerSpawns[1].Id = "first"; return () => doc.PlayerSpawns[1].Id = "second";
            case "exclusion-layer": doc.Exclusions[1].Layers = ["ghost"]; return () => doc.Exclusions[1].Layers = ["first"];
            case "override-layer": doc.ScatterOverrides[1].Layers = ["ghost"]; return () => doc.ScatterOverrides[1].Layers = ["first"];
            case "paint": doc.Surfaces.Refs[0] = doc.Surfaces.Refs[0] with { PaintPriority = 1 }; return () => doc.Surfaces.Refs[0] = doc.Surfaces.Refs[0] with { PaintPriority = 0 };
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
    }

    sealed class ChangingFeature : MapFeature
    {
        bool _read;
        public override string Type { get { string value = _read ? "lake" : "missing"; _read = true; return value; } }
    }
}
