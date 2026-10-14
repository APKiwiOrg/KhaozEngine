using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabFormatTests
{
    [Fact]
    public void SixToSeven_IsPureAndAnalyticExecutionUnchanged()
    {
        var old = JsonNode.Parse(PrefabFixtures.FormatSixJson(true))!.AsObject();
        string before = old.ToJsonString();
        var upgraded = MapPrefabMigration.Upgrade(old);
        Assert.Equal(before, old.ToJsonString());
        Assert.Equal(7, upgraded["formatVersion"]!.GetValue<int>());
        Assert.Null(upgraded["prefabs"]);
        Assert.Null(upgraded["buildingIntent"]);
        old["formatVersion"] = 7;
        Assert.True(JsonNode.DeepEquals(old, upgraded));
        Assert.Null(MapDocumentFile.LoadText(upgraded.ToJsonString()).ResolverIdentity);
        Assert.True(JsonNode.DeepEquals(upgraded, MapPrefabMigration.Upgrade(upgraded)));
    }

    [Fact]
    public void PayloadAndRoot_RoundTripMonolithicAndTiled()
    {
        var f = PrefabFixtures.Create();
        var text = MapDocumentFile.SaveText(f.Document);
        Assert.Equal(text, MapDocumentFile.SaveText(MapDocumentFile.LoadText(text)));
        var dir = PrefabFixtures.TempDirectory();
        try
        {
            MapDocumentFile.SaveTiled(f.Document, dir);
            Assert.Equal(f.Document.Prefabs!.Digest(), MapDocumentFile.LoadTiled(dir).Prefabs!.Digest());
            Assert.Equal(MapDocumentHash.OfWorld(f.Document), MapDocumentHash.OfWorld(MapDocumentFile.LoadTiled(dir)));
        }
        finally { Directory.Delete(dir, true); }
        Assert.Equal(1, MapPrefabFile.Read(PrefabFixtures.AssetBytes("valid"), "hut").PayloadVersion);
    }

    [Theory]
    [InlineData("future-payload")]
    [InlineData("unknown-member")]
    [InlineData("duplicate-member")]
    [InlineData("null-children")]
    [InlineData("nested-instance")]
    [InlineData("duplicate-child")]
    [InlineData("case-member")]
    [InlineData("missing-member")]
    [InlineData("unknown-enum")]
    [InlineData("nonpositive-scale")]
    [InlineData("embedded-record")]
    [InlineData("missing-slot")]
    [InlineData("duplicate-slot")]
    [InlineData("unsupported-slot")]
    [InlineData("wrong-payload-id")]
    [InlineData("inconsistent-pose")]
    [InlineData("missing-placement-field")]
    [InlineData("missing-light-field")]
    [InlineData("unknown-light-field")]
    [InlineData("case-light-field")]
    [InlineData("infinite-transform")]
    [InlineData("slot-scope-conflict")]
    [InlineData("record-record-pose-mismatch")]
    [InlineData("placement-numeric-id")]
    public void ClosedPrefabPayload_Refuses(string variant) =>
        Assert.Throws<MapDocumentException>(() => MapPrefabFile.Read(PrefabFixtures.AssetBytes(variant), "hut"));

    [Fact]
    public void PayloadPlacementNumericId_RefusesWriteAndOverrideValidation()
    {
        var asset = MapPrefabFile.Read(PrefabFixtures.AssetBytes("roomless"), "hut");
        var child = Assert.IsType<MapPrefabPlacementChild>(Assert.Single(asset.Children));
        child.Value.NumericId = 101;
        Assert.Throws<MapDocumentException>(() => MapPrefabFile.Write(asset));
        var f = PrefabFixtures.Create("roomless");
        var instance = Assert.Single(f.Document.Prefabs!.Instances) with
        { Overrides = new MapPrefabOverride[] { new MapPrefabReplaceOverride(child.Key, child) } };
        f.Document.Prefabs = new(1, 1, new[] { instance }, []);
        Assert.Contains(MapPrefabValidation.Validate(f.Document, f.Assets), e => e.Contains("numeric", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("record-record-pose-mismatch")]
    [InlineData("inconsistent-pose")]
    public void RecordPoseMismatch_RefusesWithThePoseConsistencyDiagnostic(string variant)
    {
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabFile.Read(PrefabFixtures.AssetBytes(variant), "hut"));
        Assert.Contains("local anchor and geometry poses must match the record pose", error.Message);
        // The same displaced geometry with World-scoped band references needs no local pose agreement.
        Assert.NotEmpty(MapPrefabFile.Read(PrefabFixtures.AssetBytes("record-record-pose-world"), "hut").Children);
        Assert.NotEmpty(MapPrefabFile.Read(PrefabFixtures.AssetBytes("valid"), "hut").Children);
    }

    [Fact]
    public void PrefabSectionConverters_AreInternalWithPublicParameterlessConstructors()
    {
        foreach (string name in new[] { "MapPrefabSetJsonConverter", "MapBuildingIntentJsonConverter", "MapStandaloneTransformsJsonConverter" })
        {
            var type = typeof(MapPrefabFile).Assembly.GetType("KhaozEngine.MapDoc.Prefabs." + name, throwOnError: true)!;
            Assert.False(type.IsPublic);
            Assert.NotNull(type.GetConstructor(Type.EmptyTypes));
        }
        var f = PrefabFixtures.Create("all-resources");
        string text = MapDocumentFile.SaveText(f.Document);
        Assert.Equal(text, MapDocumentFile.SaveText(MapDocumentFile.LoadText(text)));
    }

    [Fact]
    public void WorldRecordReference_DoesNotRequireTheLocalRecordPose()
    {
        var asset = MapPrefabFile.Read(PrefabFixtures.AssetBytes("record-record-pose-world"), "hut");
        var portal = asset.Children.OfType<MapPrefabRecordChild>().Single(c => c.Key.Key == "room-portal");
        Assert.Equal(MapPrefabReferenceScope.World, portal.References.Single(r => r.Path == "/value/bandBottom/id").Target.Scope);
        Assert.Equal(MapPrefabReferenceScope.World, portal.References.Single(r => r.Path == "/value/bandBottom/anchor/surfaceId").Target.Scope);
    }

    [Fact]
    public void FormatEight_Refuses() =>
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(PrefabFixtures.FormatSixJson(true).Replace("\"formatVersion\":6", "\"formatVersion\":8", StringComparison.Ordinal)));

    [Fact]
    public void AnalyticResolution_MatchesRecordedFormatFourBits()
    {
        var resolved = FormatFourFixtures.ResolveRecording(MapDocumentFile.Load(FormatFourFixtures.MonolithicPath), out var calls);
        FormatFourFixtures.AssertMatchesExpectations(resolved, calls);
    }

    [Theory]
    [InlineData("prefabs")]
    [InlineData("buildingIntent")]
    [InlineData("nativeResourceTransforms")]
    public void PrefabSections_RefuseResolverOneBeforeAnyCallback(string section)
    {
        var f = PrefabFixtures.Create();
        if (section != "prefabs") f.Document.Prefabs = null;
        if (section == "buildingIntent") f.Document.BuildingIntent = new(1, [], [], [], []);
        if (section == "nativeResourceTransforms") f.Document.NativeResourceTransforms = [];
        f.Document.ResolverIdentity = new(1, 1);
        f.Document.SupportRecipe = MapSupportRecipe.LegacyXzCallbackV1;
        f.Document.Surfaces = new();
        int calls = 0;
        var error = Assert.Throws<MapDocumentException>(() => MapResolver.Resolve(f.Document, f.Assets,
            (_, _) => { calls++; return 0; }, new("fixture", 1, "fixture")));
        Assert.Contains("Prefab sections require resolver identity (1, 2) and AuthoredBindingsV2.", error.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ResolverOne_WithoutPrefabSectionsCallsBackForAMissingYPlacement()
    {
        var f = PrefabFixtures.Create();
        f.Document.Prefabs = null;
        Assert.Null(f.Document.BuildingIntent);
        Assert.Null(f.Document.NativeResourceTransforms);
        f.Document.ResolverIdentity = new(1, 1);
        f.Document.SupportRecipe = MapSupportRecipe.LegacyXzCallbackV1;
        f.Document.Surfaces = new();
        f.Document.Placements.Add(new MapPlacement
        { Id = "probe", Kind = "item", AssetId = "item", X = 1, Y = null, Z = -1, Scale = 1, NumericId = ++f.Document.NumericIdHighWaterMark });
        int calls = 0;
        var resolved = MapResolver.Resolve(f.Document, f.Assets, (x, z) => { calls++; Assert.Equal((1f, -1f), (x, z)); return 2.5f; },
            new("fixture", 1, "fixture"));
        Assert.Equal(1, calls);
        Assert.Equal(2.5f, resolved.Placements.Single(p => p.PlacementId == "probe").Transform.Position.Y);
    }

    [Fact]
    public void RoomlessParentNull_RoundTripsAndRoomParentNullRefuses()
    {
        var f = PrefabFixtures.Create("roomless");
        Assert.Empty(f.Document.Surfaces.Refs);
        Assert.Null(Assert.Single(f.Document.Prefabs!.Instances).ParentSpace);
        string text = MapDocumentFile.SaveText(f.Document);
        Assert.DoesNotContain("parentSpace", text);
        Assert.Null(Assert.Single(MapDocumentFile.LoadText(text).Prefabs!.Instances).ParentSpace);
        string dir = PrefabFixtures.TempDirectory();
        try
        {
            MapDocumentFile.SaveTiled(f.Document, dir);
            Assert.Null(Assert.Single(MapDocumentFile.LoadTiled(dir).Prefabs!.Instances).ParentSpace);
        }
        finally { Directory.Delete(dir, true); }
        var room = PrefabFixtures.Create();
        var instance = room.Document.Prefabs!.Instances.Single() with { ParentSpace = null };
        room.Document.Prefabs = new(1, 1, new[] { instance }, []);
        Assert.NotEmpty(MapPrefabValidation.Validate(room.Document, room.Assets));
    }

    [Fact]
    public void AttachmentRows_UseTypedTupleKeysWhenIdsContainSeparators()
    {
        var f = PrefabFixtures.Create("roomless");
        f.Document.Prefabs = new(1, 1, f.Document.Prefabs!.Instances, [])
        {
            Attachments = new[]
            {
                new MapPrefabAttachmentTransfer("a/Support", "r", new string('a', 64), MapPrefabAttachmentKind.Light, [], true),
                new MapPrefabAttachmentTransfer("a", "Light/r", new string('a', 64), MapPrefabAttachmentKind.Support, [], true),
            }
        };
        Assert.Empty(MapPrefabValidation.Validate(f.Document, null));
        Assert.Equal(2, MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document)).Prefabs!.Attachments.Count);
    }

    [Fact]
    public void CanonicalOrder_AndDefaultOmissionsAreStable()
    {
        var f = PrefabFixtures.Create();
        var i = f.Document.Prefabs!.Instances.Single();
        f.Document.Prefabs = new(1, 1, new[] { i with { Bindings = new(i.Bindings.Entries.Reverse().ToArray()) } }, []);
        Assert.Equal(PrefabFixtures.Create().Document.Prefabs!.Digest(), f.Document.Prefabs.Digest());
        string text = MapDocumentFile.SaveText(f.Document);
        Assert.DoesNotContain("attachments", text);
        Assert.DoesNotContain("retired", text);
        var asset = f.Assets.GetPrefab("hut");
        Assert.Equal(MapPrefabFile.Write(asset), MapPrefabFile.Write(asset with { Children = asset.Children.Reverse().ToArray() }));
    }

    [Fact]
    public void AllResourceKinds_RoundTripPayloadOne()
    {
        var fixture = PrefabFixtures.Create("all-resources");
        var asset = fixture.Assets.GetPrefab("hut");
        Assert.Equal(Enum.GetValues<MapPrefabChildKind>(), asset.Children.Select(c => c.Key.Kind).Distinct().OrderBy(k => k));
        Assert.Equal(PrefabFixtures.AssetBytes("all-resources"), MapPrefabFile.Write(asset));
    }

    [Fact]
    public void ExactIdsTagsStandaloneStoreysAndTransfers_RoundTripAndHash()
    {
        var f = PrefabFixtures.Create("roomless");
        const long numeric = 9007199254740993;
        var instance = f.Document.Prefabs!.Instances.Single();
        var binding = instance.Bindings.Entries.Single() with { NumericId = numeric };
        instance = instance with { Bindings = new(new[] { binding }) };
        // A retired transfer keeps its own reservation, distinct from every live binding.
        var retired = binding with { WorldId = "retired-item", NumericId = numeric - 1 };
        f.Document.Prefabs = new(1, 1, new[] { instance },
            new[] { new MapPrefabBindingTransfer("retired", retired) { Retired = true } })
        {
            Attachments = new[] { new MapPrefabAttachmentTransfer("a/item", "support", new string('a', 64), MapPrefabAttachmentKind.Support,
                new[] { new MapPrefabAttachedChild(new(MapPrefabChildKind.Surface, "floor"), "world-floor", new("world-floor", numeric, -numeric),
                    new(1.25, -450, 3.125, 0.371, 1.2), new string('b', 64)) }, true) }
        };
        f.Document.NumericIdHighWaterMark = numeric;
        f.Document.BuildingIntent = new(1, [], [], [], new[] { new MapLocalMarkerDoc(1, "entry", "Entry", Vector3.UnitY, 0.371f, "entrance", true, new[] { "first", "second" }) })
        { Storeys = new[] { new KhaozEngine.MapDoc.Interiors.MapStoreyDoc(1, "lower", "Lower", null) } };
        f.Document.NativeResourceTransforms = new[] { new MapStandaloneResourceTransform(MapPrefabChildKind.Surface, "floor", new(1.25, -450, 3.125, 0.371, 1.2), new string('a', 64)) };
        string text = MapDocumentFile.SaveText(f.Document);
        Assert.Contains("\"9007199254740993\"", text);
        var copy = MapDocumentFile.LoadText(text);
        Assert.Equal(f.Document.Prefabs.Digest(), copy.Prefabs!.Digest());
        Assert.Equal(f.Document.BuildingIntent.Digest(), copy.BuildingIntent!.Digest());
        Assert.Equal(new[] { "first", "second" }, copy.BuildingIntent.Markers.Single().Tags);
        Assert.Equal("lower", copy.BuildingIntent.Storeys.Single().Id);
        Assert.Equal(f.Document.NativeResourceTransforms.Single(), copy.NativeResourceTransforms!.Single());
        Assert.Equal(text, MapDocumentFile.SaveText(copy));
        string dir = PrefabFixtures.TempDirectory();
        try
        {
            MapDocumentFile.SaveTiled(f.Document, dir);
            var tiled = MapDocumentFile.LoadTiled(dir);
            Assert.Equal(copy.BuildingIntent.Digest(), tiled.BuildingIntent!.Digest());
            Assert.Equal(copy.Prefabs.Digest(), tiled.Prefabs!.Digest());
            Assert.Equal(copy.NativeResourceTransforms, tiled.NativeResourceTransforms);
        }
        finally { Directory.Delete(dir, true); }
        string before = MapDocumentHash.OfWorld(f.Document);
        string rootBefore = MapSurfaceSemantics.RootDigest(f.Document);
        f.Document.BuildingIntent = new(1, [], [], [], f.Document.BuildingIntent.Markers) { Storeys = new[] { f.Document.BuildingIntent.Storeys.Single() with { Name = "Changed" } } };
        Assert.NotEqual(before, MapDocumentHash.OfWorld(f.Document));
        Assert.NotEqual(rootBefore, MapSurfaceSemantics.RootDigest(f.Document));
    }
}
