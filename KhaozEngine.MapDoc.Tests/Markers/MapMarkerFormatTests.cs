using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Markers;

public sealed class MapMarkerFormatTests
{
    // SHA-256 of the committed golden, computed outside the engine when it was captured from the released codec.
    const string GoldenSha256 = "e3fd9f15b070e359e5940a625dbb50234b85024984f24e0413a58b38e0a76993";

    [Fact]
    public void SevenToEight_IsVersionOnlyAndDoesNotSample()
    {
        var original = JsonNode.Parse(MarkerFixtures.FormatSevenJson(false))!.AsObject();
        original["surfaces"] = new JsonObject { ["sentinel"] = "unreadable" };
        string sentinelText = original.ToJsonString();
        var expectedVersionOnlyClone = (JsonObject)original.DeepClone();
        expectedVersionOnlyClone["formatVersion"] = 8;

        JsonObject upgraded = MapMarkerMigration.Upgrade(original);
        Assert.Equal(8, upgraded["formatVersion"]!.GetValue<int>());
        Assert.Equal(7, original["formatVersion"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(expectedVersionOnlyClone, upgraded));
        Assert.Equal("unreadable", upgraded["surfaces"]!["sentinel"]!.GetValue<string>());
        Assert.ThrowsAny<Exception>(() => MapDocumentFile.LoadText(sentinelText));

        // The result is an independent clone, and format 8 passes through unchanged.
        upgraded["surfaces"]!["sentinel"] = "changed";
        Assert.Equal(sentinelText, original.ToJsonString());
        Assert.True(JsonNode.DeepEquals(expectedVersionOnlyClone, MapMarkerMigration.Upgrade(expectedVersionOnlyClone)));
        var analytic = JsonNode.Parse(MarkerFixtures.FormatSevenJson(true))!.AsObject();
        JsonObject analyticEight = MapMarkerMigration.Upgrade(analytic);
        analytic["formatVersion"] = 8;
        Assert.True(JsonNode.DeepEquals(analytic, analyticEight));
        foreach (int version in new[] { 6, 9 })
        {
            analytic["formatVersion"] = version;
            Assert.Throws<MapDocumentException>(() => MapMarkerMigration.Upgrade(analytic));
        }
    }

    [Fact]
    public void PayloadOnePrefabBytes_AreUnchanged()
    {
        byte[] goldenBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Markers", "PayloadOne.prefab.json"));
        string goldenJson = Encoding.UTF8.GetString(goldenBytes);
        MapPrefabAsset read = MapPrefabFile.Read(goldenBytes, MarkerFixtures.GoldenResource);
        byte[] rewrittenBytes = MapPrefabFile.Write(read);
        Assert.Equal(goldenBytes, MapPrefabFile.Write(MarkerFixtures.PayloadOneAsset()));
        Assert.Equal(goldenBytes, MapPrefabFile.Write(MapPrefabFile.Read(goldenBytes, "golden-marker")));
        Assert.Equal(SHA256.HashData(goldenBytes), SHA256.HashData(rewrittenBytes));
        Assert.Equal(GoldenSha256, Convert.ToHexStringLower(SHA256.HashData(rewrittenBytes)));
        Assert.DoesNotContain("\"heightMode\"", goldenJson);
        Assert.DoesNotContain("\"archetypeId\"", goldenJson);
        Assert.DoesNotContain("\"supportBinding\"", goldenJson);
        MapLocalMarkerDoc child = Assert.IsType<MapPrefabMarkerChild>(Assert.Single(read.Children)).Value;
        Assert.Equal((1, (MapMarkerHeightMode?)null, (MapSupportBinding?)null, (string?)null),
            (child.PayloadVersion, child.HeightMode, child.SupportBinding, child.ArchetypeId));

        // The prefab fixture publishes exactly the golden bytes, and payload-1 world points keep their authored height.
        MarkerFixture prefab = MarkerFixtures.Create("prefab-basic");
        Assert.Equal(GoldenSha256, prefab.Assets.GetResource(MarkerFixtures.GoldenResource).Reference.Sha256);
        foreach (bool tiled in new[] { false, true })
        {
            Assert.Equal(prefab.Document.Prefabs!.Digest(), MarkerFixtures.Reload(prefab, tiled).Document.Prefabs!.Digest());
            MarkerFixture basic = MarkerFixtures.Reload(MarkerFixtures.Create(), tiled);
            MapLocalMarkerDoc reloadedMarker = Assert.Single(basic.Document.BuildingIntent!.Markers);
            var basicPoint = MarkerFixtures.Exact(reloadedMarker.Position.X, reloadedMarker.Position.Y, reloadedMarker.Position.Z);
            Assert.Equal(MarkerFixtures.Exact(-0.25f, 12.25f, -1.5f), basicPoint);
            Assert.Equal(new[] { "first", "second" }, reloadedMarker.Tags);
            Assert.Equal((1, "m", "arrival", "entry-role", true), (reloadedMarker.PayloadVersion, reloadedMarker.Id, reloadedMarker.Name, reloadedMarker.Role, reloadedMarker.Enabled));
            Assert.Equal(BitConverter.SingleToUInt32Bits(0.371f), BitConverter.SingleToUInt32Bits(reloadedMarker.YawRadians));
            Assert.Null(reloadedMarker.HeightMode);
            Assert.Null(reloadedMarker.SupportBinding);
            Assert.Null(reloadedMarker.ArchetypeId);
            string text = MapDocumentFile.SaveText(basic.Document);
            Assert.DoesNotContain("heightMode", text);
            Assert.DoesNotContain("archetypeId", text);
            Assert.DoesNotContain("supportBinding", text);
        }
    }

    [Fact]
    public void PayloadTwoBothModes_RoundTripMonolithicAndTiled()
    {
        MarkerFixture fixture = MarkerFixtures.Create("payload-two");
        string text = MapDocumentFile.SaveText(fixture.Document);
        foreach (bool tiled in new[] { false, true })
        {
            MapDocument reloaded = MarkerFixtures.Reload(fixture, tiled).Document;
            Assert.Equal(fixture.Document.BuildingIntent!.Digest(), reloaded.BuildingIntent!.Digest());
            Assert.Equal(fixture.Document.Prefabs!.Digest(), reloaded.Prefabs!.Digest());
            Assert.Equal(MapDocumentHash.OfWorld(fixture.Document), MapDocumentHash.OfWorld(reloaded));
            Assert.Equal(text, MapDocumentFile.SaveText(reloaded));
            AssertHeights(reloaded.BuildingIntent.Markers, "w-");
        }

        string directory = Path.Combine(Path.GetTempPath(), "ke-marker-" + Guid.NewGuid().ToString("N"));
        try
        {
            MapDocumentFile.SaveTiled(fixture.Document, directory);
            byte[] manifest = File.ReadAllBytes(Path.Combine(directory, "map.json"));
            MapDocumentFile.SaveTiled(MapDocumentFile.LoadTiled(directory), directory);
            Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(directory, "map.json")));
        }
        finally { Directory.Delete(directory, true); }

        byte[] prefabBytes = fixture.Assets.GetResource(MarkerFixtures.PayloadTwoResource).Bytes.ToArray();
        Assert.Equal(prefabBytes, MapPrefabFile.Write(MarkerFixtures.PayloadTwoAsset()));
        MapPrefabAsset asset = MapPrefabFile.Read(prefabBytes, MarkerFixtures.PayloadTwoResource);
        Assert.Equal(prefabBytes, MapPrefabFile.Write(asset));
        AssertHeights(asset.Children.OfType<MapPrefabMarkerChild>().Select(c => c.Value).ToArray(), "");

        var root = JsonNode.Parse(text)!.AsObject();
        var prefab = JsonNode.Parse(prefabBytes)!.AsObject();
        foreach (var (explicitMarker, surface, space) in new[]
        {
            (DocumentMarker(root, "w-explicit"), DocumentMarker(root, "w-surface"), DocumentMarker(root, "w-space")),
            (PrefabMarker(prefab, "explicit"), PrefabMarker(prefab, "surface"), PrefabMarker(prefab, "space")),
        })
        {
            Assert.Equal("Explicit", explicitMarker["heightMode"]!.GetValue<string>());
            Assert.DoesNotContain("\"supportBinding\"", explicitMarker.ToJsonString());
            Assert.Equal("""{"kind":"Surface","surfaceId":"floor"}""", surface["supportBinding"]!.ToJsonString());
            Assert.DoesNotContain("\"referenceY\"", surface.ToJsonString());
            Assert.Equal("guard", surface["archetypeId"]!.GetValue<string>());
            Assert.Equal("""{"kind":"Space","spaceId":"level-2","referenceY":20.2,"searchBelow":0.5,"searchAbove":0.5}""", space["supportBinding"]!.ToJsonString());
            Assert.DoesNotContain("\"archetypeId\"", space.ToJsonString());
        }
    }

    static void AssertHeights(IReadOnlyList<MapLocalMarkerDoc> markers, string prefix)
    {
        MapLocalMarkerDoc explicitMarker = markers.Single(m => m.Id == prefix + "explicit");
        Assert.Equal((2, (MapMarkerHeightMode?)MapMarkerHeightMode.Explicit, (MapSupportBinding?)null), (explicitMarker.PayloadVersion, explicitMarker.HeightMode, explicitMarker.SupportBinding));
        Assert.Equal(BitConverter.SingleToUInt32Bits(12.25f), BitConverter.SingleToUInt32Bits(explicitMarker.Position.Y));
        Assert.Equal(new[] { "first", "second" }, explicitMarker.Tags);
        foreach (var (id, height) in new[] { ("surface", MarkerFixtures.SurfaceHeight()), ("space", MarkerFixtures.SpaceHeight()) })
        {
            MapLocalMarkerDoc marker = markers.Single(m => m.Id == prefix + id);
            Assert.Equal((2, (MapMarkerHeightMode?)MapMarkerHeightMode.Support, height.Binding), (marker.PayloadVersion, marker.HeightMode, marker.SupportBinding));
            // The DTO carries the one canonical +0 and no height. Resolution owns the effective height.
            Assert.Equal(0u, BitConverter.SingleToUInt32Bits(marker.Position.Y));
        }
    }

    [Theory]
    [InlineData("future-payload")]
    [InlineData("unknown-member")]
    [InlineData("case-member")]
    [InlineData("duplicate-member")]
    [InlineData("binding-unknown-member")]
    [InlineData("binding-case-member")]
    [InlineData("binding-duplicate-member")]
    [InlineData("payload-two-missing-height")]
    [InlineData("payload-two-null-height")]
    [InlineData("payload-one-height")]
    [InlineData("payload-one-binding")]
    [InlineData("payload-one-archetype")]
    [InlineData("explicit-binding")]
    [InlineData("support-without-binding")]
    [InlineData("support-null-binding")]
    [InlineData("support-y-one")]
    [InlineData("support-negative-zero")]
    [InlineData("nonfinite-y")]
    [InlineData("nonfinite-yaw")]
    [InlineData("null-tags")]
    [InlineData("null-tag")]
    [InlineData("empty-archetype")]
    [InlineData("blank-archetype")]
    [InlineData("null-archetype")]
    [InlineData("unknown-height-mode")]
    [InlineData("unknown-binding-kind")]
    [InlineData("surface-reference-y")]
    [InlineData("surface-search-below")]
    [InlineData("surface-space-id")]
    [InlineData("space-missing-reference-y")]
    [InlineData("space-surface-id")]
    [InlineData("binding-null-member")]
    [InlineData("space-null-search")]
    [InlineData("negative-search")]
    [InlineData("nonfinite-search")]
    public void MarkerPayloadClosedSchema_Refuses(string variant)
    {
        var (form, mutate, duplicate) = Case(variant);
        MarkerFixture fixture = MarkerFixtures.Create("payload-two");

        // The document path runs the schema member gate, the closed codec and document validation.
        var root = JsonNode.Parse(MapDocumentFile.SaveText(fixture.Document))!.AsObject();
        Assert.NotNull(MapDocumentFile.LoadText(root.ToJsonString()));
        mutate(DocumentMarker(root, "w-" + form));
        var (documentRefusal, prefabRefusal, writeRefusal) = Refusal(variant);
        Assert.Contains(documentRefusal, Assert.Throws<MapDocumentException>(() =>
            MapDocumentFile.LoadText(Duplicated(root.ToJsonString(), duplicate))).Message, StringComparison.Ordinal);

        // The prefab payload path runs Preflight, the closed codec and asset validation.
        var asset = JsonNode.Parse(fixture.Assets.GetResource(MarkerFixtures.PayloadTwoResource).Bytes.ToArray())!.AsObject();
        Assert.NotNull(MapPrefabFile.Read(Encoding.UTF8.GetBytes(asset.ToJsonString()), MarkerFixtures.PayloadTwoResource));
        mutate(PrefabMarker(asset, form));
        Assert.Contains(prefabRefusal, Assert.Throws<MapDocumentException>(() =>
            MapPrefabFile.Read(Encoding.UTF8.GetBytes(Duplicated(asset.ToJsonString(), duplicate)), MarkerFixtures.PayloadTwoResource)).Message, StringComparison.Ordinal);

        // The same rule refuses the typed value before any write.
        if (Typed(variant, MarkerFixtures.PayloadTwoMarkers("").Single(m => m.Id == form)) is { } typed)
        {
            Assert.NotNull(writeRefusal);
            Assert.Contains(MapMarkerValidation.Validate(typed), e => e.Contains(writeRefusal, StringComparison.Ordinal));
            var invalid = MarkerFixtures.PayloadTwoAsset() with
            {
                Children = [new MapPrefabMarkerChild(new(MapPrefabChildKind.Marker, typed.Id), typed)],
            };
            Assert.Contains(writeRefusal, Assert.Throws<MapDocumentException>(() => MapPrefabFile.Write(invalid)).Message, StringComparison.Ordinal);
        }
        else Assert.Null(writeRefusal);
    }

    // The refusal each path names: the document member gate or codec, the prefab codec, and the typed rule. Each path
    // names the first rule the case violates there, so a member with the wrong case refuses as an unmapped member.
    static (string Document, string Prefab, string? Write) Refusal(string variant) => variant switch
    {
        "future-payload" => ("unknown property 'heightMode'", "unsupported local marker payload version", "unsupported local marker payload version"),
        "unknown-member" => ("unknown property 'extra'", "'extra' could not be mapped", null),
        "case-member" => ("'HeightMode' could not be mapped", "'HeightMode' could not be mapped", null),
        "duplicate-member" => ("duplicate property", "Duplicate prefab member 'heightMode'", null),
        "binding-unknown-member" => ("unknown property 'extra'", "support binding member 'extra'", null),
        "binding-case-member" => ("support binding member 'SurfaceId'", "support binding member 'SurfaceId'", null),
        "binding-duplicate-member" => ("duplicate property", "Duplicate prefab member 'surfaceId'", null),
        "payload-two-missing-height" => ("required native member 'heightMode' is missing", PayloadTwoHeight, PayloadTwoHeight),
        "payload-two-null-height" => ("heightMode: native member must not be null", "Prefab member 'heightMode' must not be null", PayloadTwoHeight),
        "payload-one-height" => ("unknown property 'heightMode'", PayloadOneMembers, PayloadOneMembers),
        "payload-one-binding" => ("unknown property 'supportBinding'", PayloadOneMembers, PayloadOneMembers),
        "payload-one-archetype" => ("unknown property 'archetypeId'", PayloadOneMembers, PayloadOneMembers),
        "explicit-binding" => Same("an Explicit height has no support binding"),
        "support-without-binding" => Same(SupportBinding),
        "support-null-binding" => ("supportBinding: native member must not be null", "Prefab member 'supportBinding' must not be null", SupportBinding),
        "support-y-one" or "support-negative-zero" => Same("a Support height stores Position.Y exactly +0"),
        "nonfinite-y" => ("vector components must be finite", "vector components must be finite", "the explicit height must be finite"),
        "nonfinite-yaw" => Same("yaw must be finite"),
        "null-tags" => ("tags: native member must not be null", NullTags, NullTags),
        "null-tag" => ("tags[1]: native member must not be null", NullTags, NullTags),
        "empty-archetype" or "blank-archetype" => Same("archetypeId must be nonblank when present"),
        "null-archetype" => ("archetypeId: native member must not be null", "Prefab member 'archetypeId' must not be null", null),
        "unknown-height-mode" => ("Unsupported exact enum name", "Unsupported exact enum name", PayloadTwoHeight),
        "unknown-binding-kind" => ("Unsupported marker support binding kind", "Unsupported marker support binding kind", "unsupported support binding kind"),
        "surface-reference-y" => ("unknown property 'referenceY'", "support binding member 'referenceY'", SurfaceOnly),
        "surface-search-below" => ("unknown property 'searchBelow'", "support binding member 'searchBelow'", SurfaceOnly),
        "surface-space-id" => ("unknown property 'spaceId'", "support binding member 'spaceId'", SurfaceOnly),
        "space-missing-reference-y" => ("required native member 'referenceY' is missing", "missing a member of its kind", SpaceRange),
        "space-surface-id" => ("unknown property 'surfaceId'", "support binding member 'surfaceId'", "a Space binding names a spaceId and no surfaceId"),
        "binding-null-member" => ("unknown property 'spaceId'", "binding member 'spaceId' must not be null", null),
        "space-null-search" => ("searchAbove: native member must not be null", "binding member 'searchAbove' must not be null", SpaceRange),
        "negative-search" => Same(SpaceRange),
        "nonfinite-search" => ("'searchAbove' must be a finite number", "'searchAbove' must be a finite number", SpaceRange),
        _ => throw new ArgumentException("Unknown marker case " + variant),
    };

    const string PayloadTwoHeight = "payload 2 requires heightMode Explicit or Support";
    const string PayloadOneMembers = "payload 1 carries no heightMode, supportBinding or archetypeId";
    const string SupportBinding = "a Support height requires a support binding";
    const string NullTags = "tags must not be null";
    const string SurfaceOnly = "a Surface binding names only a surfaceId";
    const string SpaceRange = "a Space binding requires a finite referenceY and finite nonnegative searchBelow and searchAbove";
    static (string, string, string?) Same(string message) => (message, message, message);

    static (string Form, Action<JsonObject> Mutate, string? Duplicate) Case(string variant)
    {
        static JsonObject Binding(JsonObject m) => m["supportBinding"]!.AsObject();
        static void Rename(JsonObject o, string from, string to) { JsonNode? v = o[from]; o.Remove(from); o[to] = v; }
        return variant switch
        {
            "future-payload" => ("explicit", m => m["payloadVersion"] = 3, null),
            "unknown-member" => ("explicit", m => m["extra"] = 1, null),
            "case-member" => ("explicit", m => Rename(m, "heightMode", "HeightMode"), null),
            "duplicate-member" => ("explicit", _ => { }, "\"heightMode\":\"Explicit\""),
            "binding-unknown-member" => ("surface", m => Binding(m)["extra"] = 1, null),
            "binding-case-member" => ("surface", m => Rename(Binding(m), "surfaceId", "SurfaceId"), null),
            "binding-duplicate-member" => ("surface", _ => { }, "\"surfaceId\":\"floor\""),
            "payload-two-missing-height" => ("explicit", m => m.Remove("heightMode"), null),
            "payload-two-null-height" => ("explicit", m => m["heightMode"] = null, null),
            "payload-one-height" => ("explicit", m => m["payloadVersion"] = 1, null),
            "payload-one-binding" => ("space", m => { m["payloadVersion"] = 1; m.Remove("heightMode"); }, null),
            "payload-one-archetype" => ("explicit", m => { m["payloadVersion"] = 1; m.Remove("heightMode"); m["archetypeId"] = "guard"; }, null),
            "explicit-binding" => ("explicit", m => m["supportBinding"] = JsonNode.Parse("""{"kind":"Surface","surfaceId":"floor"}"""), null),
            "support-without-binding" => ("surface", m => m.Remove("supportBinding"), null),
            "support-null-binding" => ("surface", m => m["supportBinding"] = null, null),
            "support-y-one" => ("surface", m => m["position"]!["y"] = 1, null),
            "support-negative-zero" => ("space", m => m["position"]!["y"] = JsonNode.Parse("-0.0"), null),
            "nonfinite-y" => ("explicit", m => m["position"]!["y"] = JsonNode.Parse("1e39"), null),
            "nonfinite-yaw" => ("explicit", m => m["yawRadians"] = JsonNode.Parse("-1e39"), null),
            "null-tags" => ("explicit", m => m["tags"] = null, null),
            "null-tag" => ("explicit", m => m["tags"] = new JsonArray("first", null), null),
            "empty-archetype" => ("surface", m => m["archetypeId"] = "", null),
            "blank-archetype" => ("surface", m => m["archetypeId"] = " \t", null),
            "null-archetype" => ("surface", m => m["archetypeId"] = null, null),
            "unknown-height-mode" => ("explicit", m => m["heightMode"] = "Floating", null),
            "unknown-binding-kind" => ("surface", m => Binding(m)["kind"] = "Volume", null),
            "surface-reference-y" => ("surface", m => Binding(m)["referenceY"] = 1, null),
            "surface-search-below" => ("surface", m => Binding(m)["searchBelow"] = 0.5, null),
            "surface-space-id" => ("surface", m => Binding(m)["spaceId"] = "level-2", null),
            "space-missing-reference-y" => ("space", m => Binding(m).Remove("referenceY"), null),
            "space-surface-id" => ("space", m => Binding(m)["surfaceId"] = "floor", null),
            "binding-null-member" => ("surface", m => Binding(m)["spaceId"] = null, null),
            "space-null-search" => ("space", m => Binding(m)["searchAbove"] = null, null),
            "negative-search" => ("space", m => Binding(m)["searchBelow"] = -0.5, null),
            "nonfinite-search" => ("space", m => Binding(m)["searchAbove"] = JsonNode.Parse("1e39"), null),
            _ => throw new ArgumentException("Unknown marker case " + variant),
        };
    }

    // Typed forms of the JSON cases that a DTO can express. Codec-only cases such as duplicates have none.
    static MapLocalMarkerDoc? Typed(string variant, MapLocalMarkerDoc m) => variant switch
    {
        "future-payload" => m with { PayloadVersion = 3 },
        "payload-two-missing-height" or "payload-two-null-height" => m with { HeightMode = null },
        "payload-one-height" => m with { PayloadVersion = 1 },
        "payload-one-binding" => m with { PayloadVersion = 1, HeightMode = null },
        "payload-one-archetype" => m with { PayloadVersion = 1, HeightMode = null, ArchetypeId = "guard" },
        "explicit-binding" => m with { SupportBinding = MarkerFixtures.SurfaceHeight().Binding },
        "support-without-binding" or "support-null-binding" => m with { SupportBinding = null },
        "support-y-one" => m with { Position = m.Position with { Y = 1 } },
        "support-negative-zero" => m with { Position = m.Position with { Y = -0f } },
        "nonfinite-y" => m with { Position = m.Position with { Y = float.NaN } },
        "nonfinite-yaw" => m with { YawRadians = float.NegativeInfinity },
        "null-tags" => m with { Tags = null! },
        "null-tag" => m with { Tags = new[] { "first", null! } },
        "empty-archetype" => m with { ArchetypeId = "" },
        "blank-archetype" => m with { ArchetypeId = " \t" },
        "unknown-height-mode" => m with { HeightMode = (MapMarkerHeightMode)7 },
        "unknown-binding-kind" => m with { SupportBinding = m.SupportBinding! with { Kind = (MapSupportBindingKind)9 } },
        "surface-reference-y" => m with { SupportBinding = m.SupportBinding! with { ReferenceY = 1 } },
        "surface-search-below" => m with { SupportBinding = m.SupportBinding! with { SearchBelow = 0.5f } },
        "surface-space-id" => m with { SupportBinding = m.SupportBinding! with { SpaceId = "level-2" } },
        "space-missing-reference-y" => m with { SupportBinding = m.SupportBinding! with { ReferenceY = null } },
        "space-surface-id" => m with { SupportBinding = m.SupportBinding! with { SurfaceId = "floor" } },
        "space-null-search" => m with { SupportBinding = m.SupportBinding! with { SearchAbove = null } },
        "negative-search" => m with { SupportBinding = m.SupportBinding! with { SearchBelow = -0.5f } },
        "nonfinite-search" => m with { SupportBinding = m.SupportBinding! with { SearchAbove = float.PositiveInfinity } },
        _ => null,
    };

    static string Duplicated(string json, string? member)
    {
        if (member is null) return json;
        int at = json.IndexOf(member, StringComparison.Ordinal);
        Assert.True(at >= 0 && json.IndexOf(member, at + member.Length, StringComparison.Ordinal) < 0, "Duplicated member must be unique: " + member);
        return json.Insert(at, member + ",");
    }

    [Fact]
    public void MarkerPayloadClosedSchema_PlacementNullControlLoads()
    {
        // The untouched placement definition still admits explicit nulls inside its supportBinding.
        MarkerFixture fixture = MarkerFixtures.Create("payload-two");
        MapDocument document = fixture.Document;
        var floor = new MapSurfaceRef("floor", MapLatticeFrame.ImportedMetreCentimetre, MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, "");
        document.Surfaces.Refs.Add(floor with { SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(floor, Array.Empty<KeyValuePair<MapPatchKey, string>>()) });
        var root = JsonNode.Parse(MapDocumentFile.SaveText(document))!.AsObject();
        root["placements"] = JsonNode.Parse("""
            [{"id":"crate","kind":"scenery","assetId":"crate","numericId":"1","x":1,"z":-1,"yaw":0,"scale":1,"tags":[],
              "supportBinding":{"kind":"Surface","surfaceId":"floor","spaceId":null,"referenceY":null,"searchBelow":null,"searchAbove":null}}]
            """);
        root["numericIdHighWaterMark"] = "1";
        MapPlacement placement = Assert.Single(MapDocumentFile.LoadText(root.ToJsonString()).Placements);
        Assert.Equal(new MapSupportBinding(MapSupportBindingKind.Surface, "floor", null, null, null, null), placement.SupportBinding);
    }

    [Fact]
    public void MarkerSchema_PinsPayloadShapesAndFormatEight()
    {
        var schema = JsonNode.Parse(MapDocumentSchema.GetJson())!.AsObject();
        Assert.Equal(8, schema["properties"]!["formatVersion"]!["const"]!.GetValue<int>());
        JsonNode defs = schema["$defs"]!;
        // The released payload-1 shape is the first alternative, byte for byte.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"type":"object","required":["payloadVersion","id","name","position","yawRadians","role","enabled","tags"],"additionalProperties":false,"properties":{"payloadVersion":{"type":"integer","const":1},"id":{"type":"string","minLength":1},"name":{"type":"string","minLength":1},"position":{"$ref":"#/$defs/prefabVector3"},"yawRadians":{"type":"number"},"role":{"type":"string","minLength":1},"enabled":{"type":"boolean"},"tags":{"type":"array","maxItems":1048576,"items":{"type":"string"}}}}
            """), defs["localMarker"]!["oneOf"]![0]));
        JsonNode two = defs["localMarker"]!["oneOf"]![1]!;
        Assert.Equal(2, two["properties"]!["payloadVersion"]!["const"]!.GetValue<int>());
        Assert.Contains("heightMode", two["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("#/$defs/markerSupportBinding", two["properties"]!["supportBinding"]!["$ref"]!.GetValue<string>());
        Assert.Equal(1, two["properties"]!["archetypeId"]!["minLength"]!.GetValue<int>());
        Assert.Equal(new[] { "kind,surfaceId", "kind,spaceId,referenceY,searchBelow,searchAbove" },
            defs["markerSupportBinding"]!["oneOf"]!.AsArray().Select(a => string.Join(",", a!["required"]!.AsArray().Select(n => n!.GetValue<string>()))));
        Assert.DoesNotContain("null", defs["markerSupportBinding"]!.ToJsonString());
        // The placement binding keeps its released nullable definition.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"type":["object","null"],"required":["kind"],"additionalProperties":false,"properties":{"kind":{"type":"string","enum":["Surface","Space"]},"surfaceId":{"type":["string","null"]},"spaceId":{"type":["string","null"]},"referenceY":{"type":["number","null"]},"searchBelow":{"type":["number","null"],"minimum":0},"searchAbove":{"type":["number","null"],"minimum":0}}}
            """), defs["supportBinding"]));
    }

    [Fact]
    public void SevenToEight_MovesEveryNativeIdentityOnce()
    {
        foreach (var (name, fixture) in new (string, MarkerFixture?)[]
        {
            ("marker-free", null), ("standalone", MarkerFixtures.Create()), ("prefab", MarkerFixtures.Create("prefab-basic")),
        })
        {
            MapDocument document = fixture?.Document ?? MapDocumentFile.LoadText(MarkerFixtures.FormatSevenJson(false));
            string current = MapDocumentFile.SaveText(document);
            var seven = JsonNode.Parse(current)!.AsObject();
            seven["formatVersion"] = 7;
            JsonObject versionOnly = MapMarkerMigration.Upgrade(seven);
            MapDocument migrated = MapDocumentFile.LoadText(seven.ToJsonString());
            MapDocument direct = MapDocumentFile.LoadText(versionOnly.ToJsonString());
            Assert.Equal(8, migrated.FormatVersion);
            Assert.Equal(current, MapDocumentFile.SaveText(migrated));
            Assert.Equal(current, MapDocumentFile.SaveText(direct));

            // The new pins come from the canonical version-only root, and every version-bearing identity moved.
            string[] eight = Identities(direct);
            Assert.Equal(eight, Identities(migrated));
            MapDocument atSeven = MapDocumentFile.LoadText(current);
            atSeven.FormatVersion = 7;
            string[] old = Identities(atSeven);
            for (int i = 0; i < old.Length; i++) Assert.True(old[i] != eight[i], name + " identity " + i + " did not move");
            if (fixture is not null)
                Assert.Equal(MapAuthoredIdentityV2.Compute(direct, fixture.Assets, MarkerFixtures.Options()),
                    MapAuthoredIdentityV2.Compute(migrated, fixture.Assets, MarkerFixtures.Options()));

            // Repeated load and save changes nothing.
            MapDocument again = MapDocumentFile.LoadText(MapDocumentFile.SaveText(migrated));
            Assert.Equal(current, MapDocumentFile.SaveText(again));
            Assert.Equal(eight, Identities(again));
        }

        // The stored path reads a format-7 manifest through the same version-only step.
        MarkerFixture stored = MarkerFixtures.Create("prefab-basic");
        string directory = Path.Combine(Path.GetTempPath(), "ke-marker-" + Guid.NewGuid().ToString("N"));
        try
        {
            MapDocumentFile.SaveTiled(stored.Document, directory);
            string manifestPath = Path.Combine(directory, "map.json");
            var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
            Assert.Equal(8, manifest["formatVersion"]!.GetValue<int>());
            string expected = MapAuthoredIdentityV2.Compute(stored.Document, stored.Assets, MarkerFixtures.Options());
            Assert.Equal(expected, MapAuthoredIdentityV2.Compute(MapStoredSurfaceSource.Open(directory), stored.Assets, MarkerFixtures.Options()));
            manifest["formatVersion"] = 7;
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            Assert.Equal(expected, MapAuthoredIdentityV2.Compute(MapStoredSurfaceSource.Open(directory), stored.Assets, MarkerFixtures.Options()));
            MapDocument loaded = MapDocumentFile.LoadTiled(directory);
            Assert.Equal(Identities(stored.Document), Identities(loaded));
        }
        finally { Directory.Delete(directory, true); }
    }

    // Root, manifest and world identities all carry the format version. The whole authored token composes the root.
    static string[] Identities(MapDocument document) =>
        [MapSurfaceSemantics.RootDigest(document), MapDocumentHash.OfManifest(document), MapDocumentHash.OfWorld(document)];

    [Fact]
    public void AnalyticExecutionAndOldSpawnReaders_KeepReleasedBits()
    {
        // Unchanged recorded placements and callbacks, monolithic and through the tiled reader.
        MapDocument monolithic = MapDocumentFile.Load(FormatFourFixtures.MonolithicPath);
        Assert.Equal(8, monolithic.FormatVersion);
        var resolved = FormatFourFixtures.ResolveRecording(monolithic, out var calls);
        FormatFourFixtures.AssertMatchesExpectations(resolved, calls);
        var tiled = FormatFourFixtures.ResolveRecording(MapDocumentFile.LoadTiled(FormatFourFixtures.TiledDirectory), out var tiledCalls);
        FormatFourFixtures.AssertMatchesExpectations(tiled, tiledCalls);

        // Existing analytic spawn lists pass through the version step exactly and still reach their readers.
        var seven = JsonNode.Parse(MarkerFixtures.FormatSevenJson(true))!.AsObject();
        seven["spawns"] = JsonNode.Parse("""[{"id":"wolf-1","archetypeId":"wolf","x":-3.5,"z":2.25,"enabled":false,"tags":["den"]}]""");
        seven["playerSpawns"] = JsonNode.Parse("""[{"id":"start","x":1.5,"z":-2.75,"yaw":0.371,"enabled":true,"tags":["first"]}]""");
        Assert.True(JsonNode.DeepEquals(seven["spawns"], MapMarkerMigration.Upgrade(seven)["spawns"]));
        Assert.True(JsonNode.DeepEquals(seven["playerSpawns"], MapMarkerMigration.Upgrade(seven)["playerSpawns"]));
        MapDocument analytic = MapDocumentFile.LoadText(seven.ToJsonString());
        Assert.Null(analytic.ResolverIdentity);
        Assert.Null(analytic.BuildingIntent);
        MapSpawn spawn = Assert.Single(analytic.Spawns);
        Assert.Equal(("wolf-1", "wolf", -3.5f, 2.25f, false), (spawn.Id, spawn.ArchetypeId, spawn.X, spawn.Z, spawn.Enabled));
        Assert.Equal(new[] { "den" }, spawn.Tags);
        MapPlayerSpawn start = Assert.Single(analytic.PlayerSpawns);
        Assert.Equal(("start", 1.5f, -2.75f), (start.Id, start.X, start.Z));
        Assert.Equal(BitConverter.SingleToUInt32Bits(0.371f), BitConverter.SingleToUInt32Bits(start.Yaw));
        var saved = JsonNode.Parse(MapDocumentFile.SaveText(analytic))!.AsObject();
        Assert.Equal(8, saved["formatVersion"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(seven["spawns"], saved["spawns"]));
        Assert.True(JsonNode.DeepEquals(seven["playerSpawns"], saved["playerSpawns"]));
    }

    static JsonObject DocumentMarker(JsonObject root, string id) =>
        root["buildingIntent"]!["markers"]!.AsArray().Single(m => m!["id"]!.GetValue<string>() == id)!.AsObject();

    static JsonObject PrefabMarker(JsonObject asset, string key) =>
        asset["children"]!.AsArray().Single(c => c!["key"]!["key"]!.GetValue<string>() == key)!["value"]!.AsObject();
}
