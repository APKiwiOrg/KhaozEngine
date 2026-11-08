using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapDocumentClosureFindingTests
{
    [Theory]
    [InlineData("frame", MapDocumentRefusal.SchemaValueRequired, "frame")]
    [InlineData("role", MapDocumentRefusal.SchemaEnumUnsupported, "role")]
    [InlineData("presence", MapDocumentRefusal.SchemaEnumUnsupported, "presencePolicy")]
    [InlineData("binding", MapDocumentRefusal.SchemaEnumUnsupported, "kind")]
    public void NativeMetadata_CompletesEveryFindingWithoutChangingItsProse(string fault, MapDocumentRefusal reason, string field)
    {
        var doc = MapDocumentFindingTests.ValidDocument();
        doc.ResolverIdentity = new(1, 2);
        doc.SupportRecipe = MapSupportRecipe.AuthoredBindingsV2;
        var surface = MixedResolutionFixtures.Surface("floor", 1, MapSurfaceRole.SupportFloor) with { SemanticSha256 = new string('a', 64) };
        doc.Surfaces.Refs.Add(fault switch
        {
            "frame" => surface with { Frame = null! },
            "role" => surface with { Role = (MapSurfaceRole)255 },
            "presence" => surface with { PresencePolicy = (MapPresencePolicy)255 },
            _ => surface,
        });
        if (fault == "binding") doc.Placements.Add(new() { Id = "prop", Kind = "tree", SupportBinding = new((MapSupportBindingKind)255, null, null, null, null, null) });
        var finding = Assert.Single(MapDocumentValidator.ValidateFindings(doc, MapDocRegistry.CreateDefault()));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(fault == "binding" ? "$.placements[0].supportBinding.kind" : "$.surfaces.refs[0]." + field, finding.FieldPath);
        if (fault == "frame") Assert.Null(finding.EnumType);
        else
        {
            Assert.Equal(255L, finding.ActualInteger);
            Assert.Equal(fault == "binding" ? typeof(MapSupportBindingKind) : fault == "role" ? typeof(MapSurfaceRole) : typeof(MapPresencePolicy), finding.EnumType);
        }
        if (fault == "binding") Assert.Equal("prop", finding.PlacementId);
        else Assert.Equal("floor", finding.SurfaceId);
        doc.Surfaces.Refs[0] = surface;
        if (fault == "binding") doc.Placements[0].SupportBinding = new(MapSupportBindingKind.Surface, "floor", null, null, null, null);
        Assert.Empty(MapDocumentValidator.ValidateFindings(doc, MapDocRegistry.CreateDefault()));
    }

    [Theory]
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
    [InlineData("sculpt-duplicate", MapDocumentRefusal.IdentityDuplicate, "$.sculpt[1]")]
    [InlineData("sculpt-owner", MapDocumentRefusal.DocumentTileOwnershipMismatch, "$.sculpt[0]")]
    [InlineData("placement-owner", MapDocumentRefusal.DocumentTileOwnershipMismatch, "$.placements[1]")]
    [InlineData("spawn-owner", MapDocumentRefusal.DocumentTileOwnershipMismatch, "$.spawns[1]")]
    [InlineData("player-owner", MapDocumentRefusal.DocumentTileOwnershipMismatch, "$.playerSpawns[1]")]
    public void TileAdapter_ForwardsItsFirstFindingWithTheCompleteOrderedSnapshot(string fault, MapDocumentRefusal reason, string path)
    {
        var placements = new List<MapPlacement> { new() { Id = "first", Kind = "tree" }, new() { Id = "second", Kind = "tree" } };
        var spawns = new List<MapSpawn> { new() { Id = "first", ArchetypeId = "actor" }, new() { Id = "second", ArchetypeId = "actor" } };
        var players = new List<MapPlayerSpawn> { new() { Id = "first" }, new() { Id = "second" } };
        var sculpt = new List<MapSculptTile> { new(0, 0) };
        var content = new MapTileContent(new(0, 0), placements, spawns, players, sculpt);
        switch (fault)
        {
            case "placement-id": placements[1].Id = ""; break;
            case "placement-duplicate": placements[1].Id = "first"; break;
            case "placement-kind": placements[1].Kind = ""; break;
            case "placement-scale": placements[1].Scale = 0; break;
            case "placement-nonfinite": placements[1].Scale = float.NegativeInfinity; break;
            case "spawn-id": spawns[1].Id = ""; break;
            case "spawn-duplicate": spawns[1].Id = "first"; break;
            case "spawn-archetype": spawns[1].ArchetypeId = ""; break;
            case "player-id": players[1].Id = ""; break;
            case "player-duplicate": players[1].Id = "first"; break;
            case "sculpt-duplicate": sculpt.Add(new(0, 0)); break;
            case "sculpt-owner": sculpt[0] = new(-1, 0); break;
            case "placement-owner": placements[1].X = -1; break;
            case "spawn-owner": spawns[1].X = -1; break;
            case "player-owner": players[1].X = -1; break;
        }
        var error = Assert.Throws<MapDocumentException>(() => MapTileValidator.Validate(content, "source", "tile.json", 16, 0.5f));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(path, error.FieldPath);
        Assert.Equal(new MapTileCoord(0, 0), error.Tile);
        Assert.Equal("source", error.SourceName);
        Assert.Equal("tile.json", error.Path);
        var finding = Assert.Single(error.Findings!);
        Assert.Equal(error.Reason, finding.Reason);
        Assert.Equal(path, finding.FieldPath);
        if (fault.StartsWith("sculpt-", StringComparison.Ordinal)) Assert.Equal(new MapTileCoord(fault == "sculpt-owner" ? -1 : 0, 0), finding.SculptTile);
        else Assert.Equal(fault.EndsWith("-id", StringComparison.Ordinal) ? "" : fault.EndsWith("duplicate", StringComparison.Ordinal) ? "first" : "second", error.EntityId);
        placements[1] = new() { Id = "second", Kind = "tree", X = 0 };
        spawns[1] = new() { Id = "second", ArchetypeId = "actor", X = 0 };
        players[1] = new() { Id = "second", X = 0 };
        sculpt.Clear(); sculpt.Add(new(0, 0));
        MapTileValidator.Validate(content, "source", "tile.json", 16, 0.5f);
    }

    [Fact]
    public void SculptOwnershipFinding_ReportsTheFloatPositionUsedForOwnership()
    {
        // 3 * (32 * 0.1f) rounds to 9.6f, exactly the start of document tile 1.
        var content = new MapTileContent(new(0, 0), [], [], [], [new(3, 0)]);
        var error = Assert.Throws<MapDocumentException>(() => MapTileValidator.Validate(content, "source", "tile.json", 9.6f, 0.1f));
        Assert.Equal(MapDocumentRefusal.DocumentTileOwnershipMismatch, error.Reason);
        Assert.Equal(new MapTileCoord(0, 0), error.Tile);
        Assert.Equal(new MapTileCoord(3, 0), error.SculptTile);
        Assert.Equal((double)9.6f, error.PositionX);
        Assert.Equal(0d, error.PositionZ);
        var finding = Assert.Single(error.Findings!);
        Assert.Equal(error.PositionX, finding.PositionX);
        Assert.Equal(error.PositionZ, finding.PositionZ);
        var repaired = new MapTileContent(new(1, 0), [], [], [], content.SculptTiles);
        MapTileValidator.Validate(repaired, "source", "tile.json", 9.6f, 0.1f);
    }

    [Fact]
    public void WholeDocumentAdapters_KeepEveryFindingInProducerOrder()
    {
        var doc = MapDocumentFindingTests.ValidDocument();
        doc.CompanionLayers[1].HostLayer = "ghost";
        doc.Regions[1].Shape = null;
        var findings = MapDocumentValidator.ValidateFindings(doc, MapDocRegistry.CreateDefault());
        Assert.Equal(new[] { MapDocumentRefusal.ReferenceMissing, MapDocumentRefusal.SchemaValueRequired }, findings.Select(f => f.Reason));
        Assert.Equal(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault()), findings.Select(f => f.Message));
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<MapDocumentFinding>>(findings)[0] = findings[1]);
        var options = MapDocumentFile.CreateOptions(MapDocRegistry.CreateDefault(), write: true);
        string before = JsonSerializer.Serialize(doc, options);
        var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveText(doc));
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, error.Reason);
        Assert.Equal("ghost", error.LayerId);
        Assert.Equal(findings, error.Findings);
        Assert.Equal(before, JsonSerializer.Serialize(doc, options));
        doc.CompanionLayers[1].HostLayer = "first";
        doc.Regions[1].Shape = new DiscShapeDoc();
        Assert.NotEmpty(MapDocumentFile.SaveText(doc));
    }
}
