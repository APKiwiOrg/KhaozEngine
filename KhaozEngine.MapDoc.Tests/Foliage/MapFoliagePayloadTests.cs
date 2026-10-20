using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Markers;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliagePayloadTests
{
    // SHA-256 of the released format-8 writer's bytes for each document, captured before the format advanced.
    const string ReleasedAnalyticSha256 = "ab78d53897dd9f35be25bc337467eb7854cf0ea40daf7cd8ea9b034b6d4b4ab5";
    const string ReleasedPrefabDocumentSha256 = "92251a561fbf22bfccc7157be76d06dcf1868751a41858afba017528a8906e02";

    const string AnalyticEightJson = """
        {"formatVersion":8,"id":"analytic-eight","displayName":"Analytic","bounds":{"minX":-64,"minZ":-64,"maxX":64,"maxZ":64},"tileSize":64,
         "terrain":{"seed":5,"waterLevel":-1.5,"biomes":[{"start":null,"end":10,"biome":"Meadow","baseHeight":0.5,"hillAmplitude":2}]},
         "scatterLayers":[{"name":"trees","seed":31,"cellSize":6,"jitter":1.25,"maxHeight":12,"scaleMin":0.9,"scaleMax":1.2,
           "rules":[{"biome":"Meadow","density":0.4,"kinds":[{"id":"oak","weight":2},{"id":"pine","weight":1}]}]}],
         "companionLayers":[{"name":"rocks","hostLayer":"trees","seed":7,"hostKinds":["oak"],"kinds":[{"id":"stone","weight":1}],
           "countMin":1,"countMax":3,"radiusMin":0.5,"radiusMax":1.5,"scaleMin":0.6,"scaleMax":1}],
         "exclusions":[{"name":"clearing","shape":{"type":"disc","centerX":0,"centerZ":0,"radius":8},"layers":["trees"]}],
         "scatterOverrides":[{"name":"thin","shape":{"type":"rect","minX":10,"minZ":10,"maxX":20,"maxZ":20},"densityMultiplier":0.5}],
         "placements":[{"id":"well","kind":"well","x":2.5,"z":-3.25,"yaw":0.371,"scale":1.137,"tags":["village"]}],
         "regions":[{"name":"village","shape":{"type":"rect","minX":-8,"minZ":-8,"maxX":8,"maxZ":8},"tags":["safe"]}]}
        """;

    [Fact]
    public void DensityRaster_NonSquareNegativeOriginKeepsEveryWorldAddress()
    {
        FoliageFixture fixture = FoliageFixtures.Create();
        FoliageFixture reloaded = FoliageFixtures.Reload(fixture, tiled: false);
        FoliageFixture tiled = FoliageFixtures.Reload(fixture, tiled: true);
        foreach (MapFoliageLayer layer in new[] { Single(fixture), Single(reloaded), Single(tiled) })
        {
            Assert.Equal((35, 3, MapRowDirection.PositiveZ), (layer.Width, layer.Depth, layer.RowDirection));
            Assert.Equal((byte)0, layer.DensityAt(0, 0));
            Assert.Equal((byte)104, layer.DensityAt(34, 2));
            Assert.Equal(65f, layer.OriginX + 34 * layer.CellSize);
            Assert.Equal(-1f, layer.OriginZ + 2 * layer.CellSize);
            // Every sample, at its world address. Rows advance positive world Z.
            for (int z = 0; z < 3; z++)
                for (int x = 0; x < 35; x++)
                {
                    Assert.Equal((byte)(z * 35 + x), layer.DensityAt(x, z));
                    Assert.Equal(-3f + 2f * x, layer.OriginX + x * layer.CellSize);
                    Assert.Equal(-5f + 2f * z, layer.OriginZ + z * layer.CellSize);
                }
            // Every stored byte of both chunks, including the zero padding past the domain.
            Assert.Equal(new MapFoliageChunkKey[] { new(0, 0), new(1, 0) }, layer.Chunks.Select(c => c.Key));
            foreach (MapFoliageChunk chunk in layer.Chunks)
            {
                byte[] bytes = chunk.CopyDensity();
                for (int localZ = 0; localZ < MapFoliageChunk.Size; localZ++)
                    for (int localX = 0; localX < MapFoliageChunk.Size; localX++)
                    {
                        int x = chunk.Key.X * MapFoliageChunk.Size + localX, z = chunk.Key.Z * MapFoliageChunk.Size + localZ;
                        byte expected = x < 35 && z < 3 ? (byte)(z * 35 + x) : (byte)0;
                        Assert.Equal(expected, bytes[localZ * MapFoliageChunk.Size + localX]);
                        Assert.Equal(expected, chunk.DensityAt(localX, localZ));
                    }
            }
            foreach (var (x, z) in new[] { (35, 0), (-1, 0), (0, 3), (0, -1) })
                Assert.Throws<ArgumentOutOfRangeException>(() => layer.DensityAt(x, z));
        }
        string text = MapDocumentFile.SaveText(fixture.Document);
        Assert.Equal(text, MapDocumentFile.SaveText(reloaded.Document));
        Assert.Equal(text, MapDocumentFile.SaveText(tiled.Document));
    }

    public static TheoryData<string, string?> PayloadCases()
    {
        var data = new TheoryData<string, string?>();
        foreach (var (variant, fragment) in FoliageFixtures.PayloadCases) data.Add(variant, fragment);
        return data;
    }

    [Theory]
    [MemberData(nameof(PayloadCases))]
    public void PayloadClosedSchema_RefusesMalformedAndFutureMembers(string variant, string? fragment)
    {
        string text = FoliageFixtures.Malformed(variant);
        if (fragment is null)
        {
            Assert.NotNull(MapDocumentFile.LoadText(text).Foliage);
            return;
        }
        var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(text));
        Assert.Contains(fragment, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonCeiling_RefusesOneBytePastTheLimitBeforeMaterializing()
    {
        Assert.Equal((134_217_728L, 1368, 256, 16_384, 1_048_576), (MapFoliagePayloadLimits.MaxUtf8Bytes,
            MapFoliagePayloadLimits.MaxTokenChars, MapFoliagePayloadLimits.MaxLayers, MapFoliagePayloadLimits.MaxChunksPerLayer,
            MapFoliagePayloadLimits.MaxExtent));
        Assert.Equal((67_108_864L, 65_536, 256, 65_535, 4096, 4096, 65_536), (MapFoliagePayloadLimits.MaxDensityBytes,
            MapFoliagePayloadLimits.MaxDocumentChunks, MapFoliagePayloadLimits.MaxModels, MapFoliagePayloadLimits.MaxMaterials,
            MapFoliagePayloadLimits.MaxClearanceRules, MapFoliagePayloadLimits.MaxExceptionMasks, MapFoliagePayloadLimits.MaxOwnerSupports));

        using JsonDocument json = JsonDocument.Parse(MapDocumentFile.SaveText(FoliageFixtures.Create("decorated").Document));
        JsonElement foliage = json.RootElement.GetProperty("foliage");
        int size = JsonMarshal.GetRawUtf8Value(foliage).Length;
        Assert.Single(MapFoliageJson.Read(foliage, size).Layers);
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageJson.Read(foliage, size - 1));
        Assert.Contains($"exceeds {size - 1} UTF-8 bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Converter_RefusesAnOversizedOrIncompleteSectionBeforeParsing()
    {
        byte[] section = Encoding.UTF8.GetBytes(JsonNode.Parse(MapDocumentFile.SaveText(FoliageFixtures.Create().Document))!["foliage"]!.ToJsonString());
        var options = new JsonSerializerOptions();
        MapFoliageSet Convert(long limit, bool complete)
        {
            var reader = new Utf8JsonReader(complete ? section : section.AsSpan(0, section.Length / 2), complete, default);
            reader.Read();
            return new MapFoliageSetJsonConverter(limit).Read(ref reader, typeof(MapFoliageSet), options);
        }
        Assert.Single(Convert(section.Length, complete: true).Layers);
        var over = Assert.Throws<JsonException>(() => Convert(section.Length - 1, complete: true));
        Assert.Contains($"exceeds {section.Length - 1} UTF-8 bytes", over.Message, StringComparison.Ordinal);
        var partial = Assert.Throws<JsonException>(() => Convert(section.Length, complete: false));
        Assert.Contains("fully buffered", partial.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadSchema_DeclaresTheClosedRuntimeMembersAndCeilings()
    {
        var schema = JsonNode.Parse(MapDocumentSchema.GetJson())!.AsObject();
        Assert.Equal("#/$defs/foliageInline", schema["properties"]!["foliage"]!["$ref"]!.GetValue<string>());
        JsonNode defs = schema["$defs"]!;
        JsonNode foliage = JsonNode.Parse(MapDocumentFile.SaveText(FoliageFixtures.Create("decorated").Document))!["foliage"]!;
        JsonNode layer = foliage["layers"]![0]!;
        // Schema members, in writer order, equal the members the closed codec writes. Only placementTag is optional.
        SameMembers(defs["foliageInline"]!, foliage);
        SameMembers(defs["foliageLayer"]!, layer);
        SameMembers(defs["foliageSupportBinding"]!["oneOf"]![0]!, layer["support"]!);
        SameMembers(defs["foliageSupportBinding"]!["oneOf"]![1]!, layer["ownerSupports"]![0]!["support"]!);
        SameMembers(defs["foliageModel"]!, layer["models"]![0]!);
        SameMembers(defs["foliageMaterial"]!, layer["materials"]![0]!);
        SameMembers(defs["foliagePolicy"]!, layer["policy"]!);
        SameMembers(defs["foliageCompatibilityOrdinal"]!, layer["compatibilityOrdinals"]![0]!);
        SameMembers(defs["foliageClearanceRule"]!, layer["clearanceRules"]![0]!, "placementTag");
        SameMembers(defs["foliageRect"]!, layer["clearanceRules"]![0]!["footprint"]!);
        SameMembers(defs["foliageMask"]!, layer["exceptionMasks"]![0]!);
        SameMembers(defs["foliageOwner"]!, layer["exceptionMasks"]![0]!["owner"]!);
        SameMembers(defs["foliageOwnerSupport"]!, layer["ownerSupports"]![0]!);
        SameMembers(defs["foliageChunk"]!, layer["chunks"]![0]!);

        JsonNode properties = defs["foliageLayer"]!["properties"]!;
        Assert.Equal(MapFoliagePayloadLimits.MaxLayers, defs["foliageInline"]!["properties"]!["layers"]!["maxItems"]!.GetValue<int>());
        foreach (var (name, max) in new (string, int)[]
        {
            ("models", MapFoliagePayloadLimits.MaxModels), ("allowedVisibleMaterials", MapFoliagePayloadLimits.MaxMaterials),
            ("materials", MapFoliagePayloadLimits.MaxMaterials), ("clearanceRules", MapFoliagePayloadLimits.MaxClearanceRules),
            ("exceptionMasks", MapFoliagePayloadLimits.MaxExceptionMasks), ("ownerSupports", MapFoliagePayloadLimits.MaxOwnerSupports),
            ("chunks", MapFoliagePayloadLimits.MaxChunksPerLayer),
        })
            Assert.Equal(max, properties[name]!["maxItems"]!.GetValue<int>());
        Assert.Equal(MapFoliagePayloadLimits.MaxExtent, properties["width"]!["maximum"]!.GetValue<int>());
        Assert.Equal(MapFoliagePayloadLimits.MaxExtent, properties["depth"]!["maximum"]!.GetValue<int>());
        Assert.Equal("PositiveZ", properties["rowDirection"]!["const"]!.GetValue<string>());
        Assert.Equal(new[] { "Placement", "Surface", "Record", "Marker" },
            defs["foliageOwner"]!["properties"]!["kind"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("World", defs["foliageOwner"]!["properties"]!["scope"]!["const"]!.GetValue<string>());

        // The density pattern admits exactly the canonical 1024-byte token the codec writes.
        JsonNode density = defs["foliageChunk"]!["properties"]!["density"]!;
        Assert.Equal(MapFoliagePayloadLimits.MaxTokenChars, density["maxLength"]!.GetValue<int>());
        var pattern = new Regex(density["pattern"]!.GetValue<string>());
        foreach (JsonNode? chunk in layer["chunks"]!.AsArray()) Assert.Matches(pattern, chunk!["density"]!.GetValue<string>());
        foreach (int length in new[] { 1023, 1025 }) Assert.DoesNotMatch(pattern, Convert.ToBase64String(new byte[length]));
        Assert.DoesNotMatch(pattern, Convert.ToBase64String(new byte[1024]) + "\n");
    }

    [Fact]
    public void EightToNine_IsVersionOnlyAndLeavesAbsentFoliageAbsent()
    {
        var original = JsonNode.Parse(MarkerFixtures.FormatSevenJson(false))!.AsObject();
        original["formatVersion"] = 8;
        original["surfaces"] = new JsonObject { ["sentinel"] = "unreadable" };
        string sentinelText = original.ToJsonString();
        var expectedVersionOnlyClone = (JsonObject)original.DeepClone();
        expectedVersionOnlyClone["formatVersion"] = 9;

        JsonObject upgraded = MapFoliageMigration.Upgrade(original);
        Assert.Equal(9, upgraded["formatVersion"]!.GetValue<int>());
        Assert.Equal(8, original["formatVersion"]!.GetValue<int>());
        Assert.False(upgraded.ContainsKey("foliage"));
        Assert.True(JsonNode.DeepEquals(expectedVersionOnlyClone, upgraded));
        Assert.Equal("unreadable", upgraded["surfaces"]!["sentinel"]!.GetValue<string>());
        // Positive control: the sentinel section is genuinely unreadable, and the migration never read it.
        var sentinel = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(sentinelText));
        Assert.Contains("surfaces", sentinel.Message, StringComparison.Ordinal);

        // The result is an independent clone, format 9 passes through unchanged and other versions refuse.
        upgraded["surfaces"]!["sentinel"] = "changed";
        Assert.Equal(sentinelText, original.ToJsonString());
        Assert.True(JsonNode.DeepEquals(expectedVersionOnlyClone, MapFoliageMigration.Upgrade(expectedVersionOnlyClone)));
        foreach (int version in new[] { 7, 10 })
        {
            original["formatVersion"] = version;
            Assert.Throws<MapDocumentException>(() => MapFoliageMigration.Upgrade(original));
        }

        // The loader runs the same step. Absent foliage stays absent and every version-bearing identity moves once.
        var native = JsonNode.Parse(MarkerFixtures.FormatSevenJson(false))!.AsObject();
        native["formatVersion"] = 8;
        MapDocument loaded = MapDocumentFile.LoadText(native.ToJsonString());
        MapDocument direct = MapDocumentFile.LoadText(MapFoliageMigration.Upgrade(native).ToJsonString());
        Assert.Equal(9, loaded.FormatVersion);
        Assert.Null(loaded.Foliage);
        string current = MapDocumentFile.SaveText(loaded);
        Assert.Equal(current, MapDocumentFile.SaveText(direct));
        Assert.DoesNotContain("foliage", current, StringComparison.Ordinal);
        MapDocument atEight = MapDocumentFile.LoadText(current);
        atEight.FormatVersion = 8;
        string[] nine = Identities(loaded), eight = Identities(atEight);
        for (int i = 0; i < nine.Length; i++) Assert.True(eight[i] != nine[i], "identity " + i + " did not move");
        Assert.Equal(nine, Identities(MapDocumentFile.LoadText(current)));
    }

    [Fact]
    public void OldAnalyticAndPrefabPayloadOne_KeepReleasedBytes()
    {
        string analytic = MapDocumentFile.SaveText(MapDocumentFile.LoadText(AnalyticEightJson));
        Assert.Equal(ReleasedAnalyticSha256, Sha256(AsReleased(analytic)));
        PrefabFixture prefab = PrefabFixtures.Create("all-resources");
        string prefabEight = AsReleased(MapDocumentFile.SaveText(prefab.Document));
        string prefabText = MapDocumentFile.SaveText(MapDocumentFile.LoadText(prefabEight));
        Assert.Equal(ReleasedPrefabDocumentSha256, Sha256(AsReleased(prefabText)));

        // Only the version member moves. Analytic scatter, companions and prefab payload 1 keep every byte.
        Assert.Contains("\"formatVersion\": 9", analytic, StringComparison.Ordinal);
        Assert.Contains("\"formatVersion\": 9", prefabText, StringComparison.Ordinal);
        var source = JsonNode.Parse(AnalyticEightJson)!.AsObject();
        var saved = JsonNode.Parse(analytic)!.AsObject();
        foreach (string section in new[] { "scatterLayers", "companionLayers", "exclusions", "scatterOverrides", "regions" })
            Assert.True(JsonNode.DeepEquals(source[section], saved[section]), section);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(prefabEight)!["prefabs"], JsonNode.Parse(prefabText)!["prefabs"]));
        Assert.False(saved.ContainsKey("foliage"));
        Assert.Null(MapDocumentFile.LoadText(analytic).Foliage);

        byte[] releasedPrefabBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Markers", "PayloadOne.prefab.json"));
        byte[] rewrittenPrefabBytes = MapPrefabFile.Write(MapPrefabFile.Read(releasedPrefabBytes, MarkerFixtures.GoldenResource));
        Assert.Equal(releasedPrefabBytes, rewrittenPrefabBytes);
        var resolved = FormatFourFixtures.ResolveRecording(MapDocumentFile.Load(FormatFourFixtures.MonolithicPath), out var calls);
        FormatFourFixtures.AssertMatchesExpectations(resolved, calls);
    }

    [Fact]
    public void ChunksLayersAndSets_CopyStateAndExposeNoBackingView()
    {
        Assert.Throws<ArgumentException>(() => new MapFoliageChunk(new(0, 0), new byte[1023]));
        Assert.Throws<ArgumentException>(() => new MapFoliageChunk(new(0, 0), new byte[1025]));
        var source = new byte[1024];
        source[33] = 7;
        var chunk = new MapFoliageChunk(new(1, 2), source);
        source[33] = 9;
        byte[] copy = chunk.CopyDensity();
        copy[33] = 11;
        Assert.Equal((byte)7, chunk.DensityAt(1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => chunk.DensityAt(32, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => chunk.DensityAt(0, -1));
        Type[] views = [typeof(byte[]), typeof(Memory<byte>), typeof(ReadOnlyMemory<byte>), typeof(IReadOnlyList<byte>), typeof(ArraySegment<byte>)];
        Assert.DoesNotContain(typeof(MapFoliageChunk).GetProperties(BindingFlags.Public | BindingFlags.Instance), p => views.Contains(p.PropertyType));
        Assert.Empty(typeof(MapFoliageChunk).GetFields(BindingFlags.Public | BindingFlags.Instance));

        // Collections are copied and wrapped, never returned as the caller's list or an array.
        var chunks = new List<MapFoliageChunk>(FoliageFixtures.BasicChunks());
        MapFoliageLayer layer = FoliageFixtures.BasicLayer(chunks);
        chunks.Clear();
        Assert.Equal(2, layer.Chunks.Count);
        Assert.IsType<ReadOnlyCollection<MapFoliageChunk>>(layer.Chunks);
        Assert.IsType<ReadOnlyCollection<MapFoliageModel>>(layer.Models);
        Assert.IsType<ReadOnlyCollection<ushort>>(layer.AllowedVisibleMaterials);
        Assert.IsType<ReadOnlyCollection<MapFoliageMaterial>>(layer.Materials);
        Assert.IsType<ReadOnlyCollection<MapFoliageCompatibilityOrdinal>>(layer.CompatibilityOrdinals);
        var layers = new List<MapFoliageLayer> { layer };
        var set = new MapFoliageSet(1, layers);
        layers.Clear();
        Assert.IsType<ReadOnlyCollection<MapFoliageLayer>>(set.Layers);
        Assert.Single(set.Layers);

        // An absent chunk reads zero, and an explicit all-zero chunk is kept as authored.
        MapFoliageLayer sparse = FoliageFixtures.BasicLayer([FoliageFixtures.BasicChunk(1)]);
        Assert.Equal((byte)0, sparse.DensityAt(5, 1));
        Assert.Equal((byte)68, sparse.DensityAt(33, 1));
        FoliageFixture fixture = FoliageFixtures.Create();
        fixture.Document.Foliage = new MapFoliageSet(1,
            [FoliageFixtures.BasicLayer([new MapFoliageChunk(new(0, 0), new byte[1024]), FoliageFixtures.BasicChunk(1)])]);
        MapFoliageLayer zero = Single(FoliageFixtures.Reload(fixture, tiled: false));
        Assert.Equal(2, zero.Chunks.Count);
        Assert.All(zero.Chunks[0].CopyDensity(), b => Assert.Equal((byte)0, b));

        // Clone owns new set and layer objects with identical bytes, and layers keep their authored order.
        fixture.Document.Foliage = new MapFoliageSet(1, [Renamed("z-layer"), Renamed("a-layer")]);
        MapFoliageSet clone = fixture.Document.Foliage.Clone();
        Assert.NotSame(fixture.Document.Foliage, clone);
        Assert.NotSame(fixture.Document.Foliage.Layers[0], clone.Layers[0]);
        Assert.Equal(Write(fixture.Document.Foliage), Write(clone));
        Assert.Equal(new[] { "z-layer", "a-layer" },
            FoliageFixtures.Reload(fixture, tiled: false).Document.Foliage!.Layers.Select(l => l.Id));
    }

    [Fact]
    public void CompatibilityOrdinals_SurviveCodecSchemaAndCloneAndPermitAnEmptyTable()
    {
        FoliageFixture fixture = FoliageFixtures.Create("decorated");
        MapFoliageLayer before = Single(fixture);
        MapFoliageLayer after = Single(FoliageFixtures.Reload(fixture, tiled: false));
        var expected = new MapFoliageCompatibilityOrdinal[] { new(FoliageFixtures.FloorId, 0), new("upper", 0) };
        Assert.Equal(expected, before.CompatibilityOrdinals);
        Assert.Equal(expected, after.CompatibilityOrdinals);
        Assert.Equal(expected, Assert.Single(fixture.Document.Foliage!.Clone().Layers).CompatibilityOrdinals);
        Assert.Equal(before.ClearanceRules, after.ClearanceRules);
        Assert.Equal(before.ExceptionMasks, after.ExceptionMasks);
        Assert.Equal(before.OwnerSupports, after.OwnerSupports);
        Assert.Equal(Write(fixture.Document.Foliage!), Write(fixture.Document.Foliage!.Clone()));
        MapBoundDocumentValidation.Validate(fixture.Document, fixture.Assets);

        // An empty table is legal, the canonical fallback for every owner.
        var root = JsonNode.Parse(MapDocumentFile.SaveText(fixture.Document))!.AsObject();
        Layer(root)["compatibilityOrdinals"] = new JsonArray();
        MapDocument empty = MapDocumentFile.LoadText(root.ToJsonString());
        Assert.Empty(Assert.Single(empty.Foliage!.Layers).CompatibilityOrdinals);
        MapBoundDocumentValidation.Validate(empty, fixture.Assets);
    }

    [Fact]
    public void BoundValidation_ChecksMetadataRolesWithoutExpanding()
    {
        FoliageFixture fixture = FoliageFixtures.Create("decorated");
        long expansions = MapPrefabResolver.ThreadExpansions;
        Assert.Empty(MapFoliageValidation.ValidateBound(fixture.Document, fixture.Assets));
        foreach (var (mutate, fragment) in new (Action<JsonObject>, string)[]
        {
            (l => l["models"]![1]!["assetId"] = "reed-c", "model asset 'reed-c' is not in the verified closure"),
            (l => l["materials"]![0]!["resourceId"] = "mesh", "resource 'mesh' is not a Material resource"),
            (l => l["materials"]![2]!["resourceId"] = "absent", "resource 'absent' is not a Material resource"),
            (l => l["clearanceRules"]![0]!["assetId"] = "absent", "asset 'absent' is not in the verified closure"),
            (l => l["support"]!["surfaceId"] = "ghost", "surface 'ghost' is not a declared or bound surface"),
            (l => l["compatibilityOrdinals"]![1]!["surfaceId"] = "ghost", "surface 'ghost' is not a declared or bound surface"),
        })
        {
            var root = JsonNode.Parse(MapDocumentFile.SaveText(fixture.Document))!.AsObject();
            mutate(Layer(root));
            MapDocument document = MapDocumentFile.LoadText(root.ToJsonString());
            Assert.Contains(MapFoliageValidation.ValidateBound(document, fixture.Assets), e => e.Contains(fragment, StringComparison.Ordinal));
            var error = Assert.Throws<MapDocumentException>(() => MapBoundDocumentValidation.Validate(document, fixture.Assets));
            Assert.Contains(fragment, error.Message, StringComparison.Ordinal);
        }
        // A declared surface that is not a support floor refuses as a compatibility row.
        MapDocument ceiling = FoliageFixtures.Reload(fixture, tiled: false).Document;
        int upper = ceiling.Surfaces.Refs.FindIndex(s => s.Id == "upper");
        ceiling.Surfaces.Refs[upper] = ceiling.Surfaces.Refs[upper] with { Role = MapSurfaceRole.Ceiling };
        Assert.Contains(MapFoliageValidation.ValidateBound(ceiling, fixture.Assets),
            e => e.Contains("surface 'upper' is not a support floor", StringComparison.Ordinal));

        // A bound prefab surface is known by its persisted world ID, without expanding the instance.
        PrefabFixture prefab = PrefabFixtures.Create();
        string bound = prefab.Document.Prefabs!.Instances.SelectMany(i => i.Bindings.Entries)
            .First(b => b.Child.Kind == MapPrefabChildKind.Surface && !b.Removed).WorldId;
        prefab.Document.Foliage = new MapFoliageSet(1, [Renamed(FoliageFixtures.LayerId, FoliageFixtures.Surface(bound))]);
        IReadOnlyList<string> prefabErrors = MapFoliageValidation.ValidateBound(prefab.Document, prefab.Assets);
        Assert.DoesNotContain(prefabErrors, e => e.Contains(" support: surface", StringComparison.Ordinal));
        // Control: the layer's compatibility row still names the absent fixture floor.
        Assert.Contains(prefabErrors,
            e => e.Contains("compatibility ordinal: surface 'floor' is not a declared or bound surface", StringComparison.Ordinal));
        Assert.Equal(expansions, MapPrefabResolver.ThreadExpansions);
    }

    [Fact]
    public void Foliage_RequiresResolverTwoWithAdoptionGuidance()
    {
        MapDocument analytic = MapDocumentFile.LoadText(AnalyticEightJson);
        analytic.Foliage = FoliageFixtures.Create().Document.Foliage;
        Assert.Contains(MapDocumentValidator.Validate(analytic, MapDocRegistry.CreateDefault()),
            e => e.Contains("MapResolverAdoption.ConvertToAuthoredSupport", StringComparison.Ordinal));
        var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveText(analytic));
        Assert.Contains("foliage: requires resolver identity (1, 2) and supportRecipe AuthoredBindingsV2", error.Message, StringComparison.Ordinal);
        FoliageFixture fixture = FoliageFixtures.Create();
        fixture.Document.SupportRecipe = MapSupportRecipe.LegacyXzCallbackV1;
        Assert.Contains(MapDocumentValidator.Validate(fixture.Document, MapDocRegistry.CreateDefault()),
            e => e.StartsWith("foliage: requires resolver identity", StringComparison.Ordinal));
    }

    static MapFoliageLayer Renamed(string id, MapSupportBinding? support = null)
    {
        MapFoliageLayer basic = FoliageFixtures.BasicLayer();
        return new MapFoliageLayer(basic.PayloadVersion, id, support ?? basic.Support, basic.OriginX, basic.OriginZ, basic.CellSize, basic.Width,
            basic.Depth, basic.RowDirection, basic.Seed, basic.Spacing, basic.ScaleMin, basic.ScaleMax, basic.RootOffset, basic.Models,
            basic.AllowedVisibleMaterials, basic.Materials, basic.Policy, basic.ExcludeIndoors, basic.ExcludeSolidObjects,
            basic.DoorClearance, basic.EdgeFade, basic.CompatibilityOrdinals, basic.ClearanceRules, basic.ExceptionMasks,
            basic.OwnerSupports, basic.Chunks);
    }

    static void SameMembers(JsonNode definition, JsonNode written, params string[] optional)
    {
        Assert.False(definition["additionalProperties"]!.GetValue<bool>());
        string[] declared = definition["properties"]!.AsObject().Select(p => p.Key).ToArray();
        Assert.Equal(declared, written.AsObject().Select(p => p.Key));
        Assert.Equal(declared.Except(optional), definition["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    static JsonObject Layer(JsonObject root) => root["foliage"]!["layers"]![0]!.AsObject();

    static MapFoliageLayer Single(FoliageFixture fixture) => Assert.Single(fixture.Document.Foliage!.Layers);

    static string Write(MapFoliageSet set)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) MapFoliageJson.Write(writer, set);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // Root, manifest and world identities all carry the format version.
    static string[] Identities(MapDocument document) =>
        [MapSurfaceSemantics.RootDigest(document), MapDocumentHash.OfManifest(document), MapDocumentHash.OfWorld(document)];

    static string AsReleased(string text) => text.Replace("\"formatVersion\": 9", "\"formatVersion\": 8", StringComparison.Ordinal);

    static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
