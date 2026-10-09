using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class FormatAdvanceTests
{
    static MapDocument Migrated() => MapDocumentFile.Load(FormatFourFixtures.MonolithicPath);

    [Fact]
    public void FormatAdvance_PreservesResolverV1Execution_Monolithic()
    {
        MapDocument doc = Migrated();
        Assert.Equal((5, MapSupportRecipe.LegacyXzCallbackV1), (doc.FormatVersion, doc.SupportRecipe));
        MapResolvedDocument r = FormatFourFixtures.ResolveRecording(doc, out var calls);
        FormatFourFixtures.AssertMatchesExpectations(r, calls);
    }

    [Fact]
    public void FormatAdvance_TiledWindowCarriesTheDocumentRecipeToUnloadedPlacements()
    {
        string dir = FormatFourFixtures.CopyTiledToTemp();
        try
        {
            var tiles = FormatFourFixtures.TileFileDigests(dir);
            MapDocument window = MapDocumentFile.LoadTiled(dir, new MapTileRect(new(0, 0), new(0, 0)));
            Assert.DoesNotContain(window.Placements, p => p.Id == "a");
            MapDocumentFile.SaveTiled(window, dir);
            string manifest = File.ReadAllText(Path.Combine(dir, "map.json"));
            Assert.Contains("\"formatVersion\": 5", manifest);
            Assert.Contains("\"supportRecipe\": \"LegacyXzCallbackV1\"", manifest);
            Assert.Equal(tiles, FormatFourFixtures.TileFileDigests(dir));
            MapResolvedDocument r = FormatFourFixtures.ResolveRecording(MapDocumentFile.LoadTiled(dir), out var calls);
            FormatFourFixtures.AssertMatchesExpectations(r, calls);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FormatAdvance_IdentityTokenChangesExactlyOnce()
    {
        MapDocument migrated = Migrated();
        string once = MapAuthoredIdentity.Compute(migrated, FormatFourFixtures.Assets(), FormatFourFixtures.Options);
        Assert.NotEqual(FormatFourFixtures.LoadExpectations().AuthoredHashFormatFour, once);
        Assert.Equal(once, MapAuthoredIdentity.Compute(
            MapDocumentFile.LoadText(MapDocumentFile.SaveText(migrated)),
            FormatFourFixtures.Assets(), FormatFourFixtures.Options));
    }

    [Fact]
    public void FormatFourObjectInMemory_FailsCurrentValidation()
    {
        MapDocument doc = Migrated();
        doc.FormatVersion = 4;
        Assert.Contains(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault()),
            e => e.Contains("formatVersion is 4, expected 5"));
    }

    [Fact]
    public void ResolverV1Overload_RefusesAVersionTwoDocumentBeforeAnyCallback()
    {
        MapDocument doc = Migrated();
        doc.ResolverIdentity = new(1, 2);
        doc.SupportRecipe = MapSupportRecipe.AuthoredBindingsV2;
        int calls = 0;
        MapDocumentException error = Assert.Throws<MapDocumentException>(() => MapResolver.Resolve(doc, FormatFourFixtures.Assets(),
            (x, z) => { calls++; return 0f; }, FormatFourFixtures.Options));
        Assert.Contains("MapAuthoredIdentityV2", error.Message);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(1, MapSupportRecipe.AuthoredBindingsV2, false)]
    [InlineData(2, MapSupportRecipe.LegacyXzCallbackV1, false)]
    [InlineData(1, MapSupportRecipe.LegacyXzCallbackV1, true)]
    public void SupportRecipe_PairsWithResolverVersionAndRefusesExplicitYWithBinding(
        int resolver, MapSupportRecipe recipe, bool bindExplicitY)
    {
        MapDocument doc = Migrated();
        doc.ResolverIdentity = new(1, resolver);
        doc.SupportRecipe = recipe;
        if (bindExplicitY)
            doc.Placements.Single(p => p.Id == "b").SupportBinding =
                new(MapSupportBindingKind.Surface, "ground", null, null, null, null);
        Assert.NotEmpty(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault()));
    }

    [Fact]
    public void AnalyticFormatFour_MigratesWithoutNativeAdditions()
    {
        string text = MapDocumentFile.SaveText(MapDocumentFile.LoadText(NativeFixtures.AnalyticV3Json()));
        Assert.Contains("\"formatVersion\": 5", text);
        Assert.Contains("\"supportRecipe\": \"LegacyXzCallbackV1\"", text);
        Assert.Contains("\"surfaces\": []", text);
        Assert.DoesNotContain("supportBinding", text);
    }

    [Theory]
    [InlineData(1, MapSupportRecipe.LegacyXzCallbackV1)]
    [InlineData(2, MapSupportRecipe.AuthoredBindingsV2)]
    public void LocalValidation_AcceptsBothSupportedResolverRecipePairs(int resolver, MapSupportRecipe recipe)
    {
        MapDocument doc = Migrated();
        doc.ResolverIdentity = new(1, resolver);
        doc.SupportRecipe = recipe;
        Assert.Empty(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault()));
        MapBoundDocumentValidation.ValidateLocal(doc);
    }

    [Fact]
    public void AuthoredBinding_RefusesOnlyWhenTheSameValidPlacementAlsoHasExplicitY()
    {
        MapDocument doc = Migrated();
        doc.ResolverIdentity = new(1, 2);
        doc.SupportRecipe = MapSupportRecipe.AuthoredBindingsV2;
        var ground = new MapSurfaceRef("ground", MapLatticeFrame.ImportedMetreCentimetre,
            MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, "");
        ground = ground with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(ground, Array.Empty<KeyValuePair<MapPatchKey, string>>()),
        };
        doc.Surfaces.Refs.Add(ground);
        MapPlacement placement = doc.Placements.Single(p => p.Id == "b");
        float? explicitY = placement.Y;
        Assert.NotNull(explicitY);
        placement.Y = null;
        placement.SupportBinding = new(MapSupportBindingKind.Surface, "ground", null, null, null, null);
        Assert.Empty(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault()));
        MapBoundDocumentValidation.ValidateLocal(doc);
        placement.Y = explicitY;
        Assert.NotEmpty(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault()));
        Assert.Throws<MapDocumentException>(() => MapBoundDocumentValidation.ValidateLocal(doc));
    }

    [Fact]
    public void SurfaceMetadataAndBindingRoundTripWithoutResidentPatchStorage()
    {
        MapDocument doc = Migrated();
        doc.ResolverIdentity = new(1, 2);
        doc.SupportRecipe = MapSupportRecipe.AuthoredBindingsV2;
        var surface = new MapSurfaceRef("floor", new(new(1, 3), new(1, 200), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0),
            MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, "");
        surface = surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, Array.Empty<KeyValuePair<MapPatchKey, string>>()),
        };
        doc.Surfaces.Refs.Add(surface);
        MapPlacement placement = doc.Placements.Single(p => p.Id == "a");
        Assert.Null(placement.Y);
        var binding = new MapSupportBinding(MapSupportBindingKind.Surface, "floor", null, 0.25f, 1f, 2f);
        placement.SupportBinding = binding;
        MapDocument copy = MapDocumentFile.LoadText(MapDocumentFile.SaveText(doc));
        Assert.Equal(MapSupportRecipe.AuthoredBindingsV2, copy.SupportRecipe);
        Assert.Equal(surface, Assert.Single(copy.Surfaces.Refs));
        Assert.Equal(binding, copy.Placements.Single(p => p.Id == "a").SupportBinding);
        Assert.Empty(copy.Surfaces.Patches);
    }

    [Fact]
    public void SurfaceSetCloneOwnsNestedCollectionsAndRecordLookupUsesOnlyItsAnchor()
    {
        Assert.True(new MapSurfaceSet().IsEmpty);
        TopologyWorld world = TopologyRecordFixtures.LegacyExteriorWorld();
        MapSurfacePatch patch = world.Patches.Single();
        patch.Records.Reverse();
        string[] tags = { "inside" };
        var set = new MapSurfaceSet();
        set.Refs.Add(world.Surfaces.Single() with
        {
            IndoorSpan = new("inside", new("world", patch.Key), 0, 300, tags),
        });
        set.Patches.Add(patch.Key, patch);
        MapSurfaceSet copy = set.Clone();
        tags[0] = "changed";
        patch.Heights[0] = 321;
        patch.Records.Clear();
        Assert.False(copy.IsEmpty);
        Assert.Equal("inside", Assert.Single(copy.Refs.Single().IndoorSpan!.DomainTags));
        Assert.Equal(0, copy.Patches[patch.Key].Heights[0]);
        Assert.Equal(new[] { "world", "world-cells" }, copy.AllRecords().Select(r => r.Id));
        Assert.True(copy.TryGetRecord(new("world", patch.Key), out MapTopologyRecord? record));
        Assert.IsType<MapSpaceDoc>(record);
        Assert.False(copy.TryGetRecord(new("world", new("plane-0", 1, 0)), out _));
    }
}
