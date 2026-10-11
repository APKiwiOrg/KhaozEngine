using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Tests.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public class MapWaterEditTests
{
    [Fact]
    public void Upsert_ReportsBodyBoundsAndMediumInvalidation_AndStaysPure()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapWaterBodyDoc raised = doc.Water!.Bodies.Single(b => b.Id == "cave-lake") with { SurfaceY = -33f };
        MapWaterEditResult r = MapWaterEdits.Prepare(doc, new MapUpsertWaterBody(raised));
        Assert.Equal(new[] { "cave-lake" }, r.Effects.WaterBodyIds);
        Assert.Equal(new[] { "lake-cave" }, r.Effects.SpaceIds);
        Assert.True(r.Effects.Invalidates.HasFlag(MapNativeInvalidation.Medium));
        Assert.Equal((-34d, -33d), (r.Effects.OldBounds!.Value.MaxY, r.Effects.NewBounds!.Value.MaxY));
        Assert.True(r.WriteSet.Water);
        Assert.Equal(-34f, doc.Water.Bodies.Single(b => b.Id == "cave-lake").SurfaceY);
    }

    [Fact]
    public void Remove_ReportsNoNewBounds()
    {
        MapWaterEditResult r = MapWaterEdits.Prepare(WaterFixtures.OceanOverCaves(), new MapRemoveWaterBody("flooded"));
        Assert.DoesNotContain(r.After!.Bodies, b => b.Id == "flooded");
        Assert.Null(r.Effects.NewBounds);
    }

    [Theory]
    [InlineData("remove-used-medium", "water medium 'sea-water' is used by 'cave-lake'")]
    [InlineData("legacy-with-bodies", "remove every water body before using legacy global water")]
    [InlineData("remove-unknown-body", "no water body 'nowhere'")]
    [InlineData("upsert-without-section", "bounded water is off")]
    [InlineData("bounded-on-resolver-one", "bounded water requires a resolver-2 native document")]
    public void InvalidEdits_Refuse(string variant, string message) =>
        Assert.Contains(message, Assert.Throws<MapDocumentException>(() => WaterFixtures.ApplyEdit(variant)).Message, StringComparison.Ordinal);

    [Fact]
    public void NameAffectedBodies_UsesCoveredSpaces()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapNativeEditEffects terrain = MapTerrainEdits.Prepare(doc.Surfaces, WaterFixtures.LowerSeabedAt(2, -2, metres: 1f)).Effects;
        MapNativeEditEffects named = MapWaterEdits.NameAffectedBodies(doc.Water, terrain, doc.Surfaces);
        Assert.Contains("ocean", named.WaterBodyIds);
        Assert.DoesNotContain("cave-lake", named.WaterBodyIds);
        Assert.True(named.Invalidates.HasFlag(MapNativeInvalidation.Medium));
    }
}

public sealed class WaterEditEffectsTests
{
    [Fact]
    public void BodyReplacement_ReportsBothCoversBoundsAndDefinitionDigests()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapWaterBodyDoc before = doc.Water!.Bodies.Single(b => b.Id == "cave-lake");
        MapWaterBodyDoc after = before with
        {
            SurfaceY = -55f,
            RenderMaterial = "material",
            Covers = new[] { new MapRecordRef("dry-cave", new("dry-floor", 0, 0)) },
        };
        MapWaterEditResult result = MapWaterEdits.Prepare(doc, new MapUpsertWaterBody(after));
        Assert.Equal(new[] { "dry-cave", "lake-cave" }, result.Effects.SpaceIds);
        Assert.Equal(new MapBox3(0, -34, -4, 6, -34, 0), result.Effects.OldBounds);
        Assert.Equal(new MapBox3(0, -55, -4, 6, -55, 0), result.Effects.NewBounds);
        Assert.Equal(new MapDigestChange("water:cave-lake", MapWaterSet.BodyDigest(before), MapWaterSet.BodyDigest(after)),
            Assert.Single(result.Effects.DigestChanges));
        Assert.Equal(MapNativeInvalidation.Medium | MapNativeInvalidation.Residency | MapNativeInvalidation.Nav |
            MapNativeInvalidation.Material, result.Effects.Invalidates);
        Assert.Empty(result.WriteSet.Patches);
        Assert.Empty(result.WriteSet.SurfaceIds);
        Assert.Empty(result.WriteSet.RecordIds);
        Assert.Empty(result.WriteSet.SpaceIds);
        Assert.Empty(result.WriteSet.OwnerChanges);
        Assert.Empty(result.WriteSet.Materials);
        Assert.False(result.WriteSet.Placements);
        Assert.False(result.WriteSet.NativeAssets);
    }

    [Fact]
    public void BodyAddAndRemove_ReportMaterialAndOneSidedBounds()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapWaterBodyDoc added = doc.Water!.Bodies.Single(b => b.Id == "cave-lake") with
        {
            Id = "dry-lake",
            SurfaceY = -55f,
            Covers = new[] { new MapRecordRef("dry-cave", new("dry-floor", 0, 0)) },
        };
        MapWaterEditResult add = MapWaterEdits.Prepare(doc, new MapUpsertWaterBody(added));
        Assert.Equal(4, add.After!.Bodies.Count);
        Assert.Null(add.Effects.OldBounds);
        Assert.Equal(new MapBox3(0, -55, -4, 6, -55, 0), add.Effects.NewBounds);
        Assert.True(add.Effects.Invalidates.HasFlag(MapNativeInvalidation.Material));
        Assert.Null(Assert.Single(add.Effects.DigestChanges).Before);
        doc.Water = add.After;
        MapWaterEditResult remove = MapWaterEdits.Prepare(doc, new MapRemoveWaterBody("dry-lake"));
        Assert.Equal(add.Effects.NewBounds, remove.Effects.OldBounds);
        Assert.Null(remove.Effects.NewBounds);
        Assert.True(remove.Effects.Invalidates.HasFlag(MapNativeInvalidation.Material));
        Assert.Null(Assert.Single(remove.Effects.DigestChanges).After);
    }

    [Fact]
    public void MediumEdit_NamesEveryUsingBodyAndLeavesTheSourceUntouched()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapWaterEditResult result = MapWaterEdits.Prepare(doc,
            new MapUpsertWaterMedium(new("sea-water", MapWaterMediumKind.Water, 0.5f)));
        Assert.Equal(new[] { "cave-lake", "flooded", "ocean" }, result.Effects.WaterBodyIds);
        Assert.Equal(new[] { "flooded-cave", "lake-cave", "sea" }, result.Effects.SpaceIds);
        Assert.True(result.Effects.Invalidates.HasFlag(MapNativeInvalidation.Medium));
        Assert.Equal(0.5f, Assert.Single(result.After!.Media).WadeSpeedScale);
        Assert.Equal(1f, Assert.Single(doc.Water!.Media).WadeSpeedScale);
    }

    [Fact]
    public void UnusedMedium_CanBeAddedAndRemoved()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        string before = doc.Water!.Digest();
        MapWaterEditResult add = MapWaterEdits.Prepare(doc,
            new MapUpsertWaterMedium(new("pond-water", MapWaterMediumKind.Water, 1f)));
        Assert.Empty(add.Effects.WaterBodyIds);
        doc.Water = add.After;
        Assert.Equal(before, MapWaterEdits.Prepare(doc, new MapRemoveWaterMedium("pond-water")).After!.Digest());
    }

    [Fact]
    public void BoundedSwitch_CreatesAnEmptySectionAndCanReturnToLegacy()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        doc.Water = null;
        MapWaterEditResult bounded = MapWaterEdits.Prepare(doc, new MapSetBoundedWater(true));
        Assert.NotNull(bounded.After);
        Assert.Empty(bounded.After.Bodies);
        Assert.Empty(bounded.After.Media);
        Assert.Null(doc.Water);
        Assert.Equal(MapNativeInvalidation.Medium | MapNativeInvalidation.Unbounded, bounded.Effects.Invalidates);
        Assert.True(bounded.WriteSet.Water);
        doc.Water = bounded.After;
        MapWaterEditResult legacy = MapWaterEdits.Prepare(doc, new MapSetBoundedWater(false));
        Assert.Null(legacy.After);
        Assert.Equal(bounded.Effects.Invalidates, legacy.Effects.Invalidates);
    }

    [Fact]
    public void Prepare_DeepCopiesInputBodiesAndEditCollections()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapWaterBodyDoc body = doc.Water!.Bodies.Single(b => b.Id == "cave-lake");
        MapWaterEditResult result = MapWaterEdits.Prepare(doc, new MapUpsertWaterBody(body with { SurfaceY = -33f }));
        string after = result.After!.Digest();
        ((MapWaterRect[])body.Domain.Rects)[0] = new(40, 0, 46, 4);
        ((MapRecordRef[])body.Covers)[0] = new("sea", new("seabed", 0, 0));
        doc.Water.Media.Clear();
        Assert.Equal(after, result.After.Digest());
    }

    [Fact]
    public void NamingBodies_MergesExistingIdsAndKeepsUnrelatedEffects()
    {
        var effects = new MapNativeEditEffects(null, null, Array.Empty<MapPatchKey>(), new[] { "sea" },
            Array.Empty<string>(), Array.Empty<MapDigestChange>(), MapNativeInvalidation.Terrain)
        {
            WaterBodyIds = new[] { "ocean", "previous" },
        };
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapNativeEditEffects named = MapWaterEdits.NameAffectedBodies(doc.Water, effects, doc.Surfaces);
        Assert.Equal(new[] { "ocean", "previous" }, named.WaterBodyIds);
        Assert.Equal(MapNativeInvalidation.Terrain | MapNativeInvalidation.Medium, named.Invalidates);
        Assert.Equal(effects, MapWaterEdits.NameAffectedBodies(null, effects, doc.Surfaces));
    }
}
