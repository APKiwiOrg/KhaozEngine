using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDocCompatibility;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
internal sealed class ShippedSceneGuardFixture(string root, Dictionary<string, string?> environment) : IDisposable
{
    internal IReadOnlyDictionary<string, string?> Environment => environment;
    internal string PublicSecretMarker => ShippedSceneGuardFixtures.Sentinel;
    internal string SourceRoot => environment["KHAOZ_R2_SHIPPED_SOURCE"]!;
    internal string ReportPath => environment["KHAOZ_R2_ORACLE_REPORT"]!;
    internal IReadOnlyList<string> FailureCategories() => File.ReadAllLines(ReportPath).Select(line =>
        JsonDocument.Parse(line).RootElement.GetProperty("category").GetString()!).Where(c => c is not "scene-summary" and not "scene-buildings" and not "scene-markers").ToArray();
    public void Dispose() => Directory.Delete(root, true);
}

[UnsupportedOSPlatform("windows")]
internal static partial class ShippedSceneGuardFixtures
{
    internal const string Sentinel = "PUBLIC-INVENTED-SCENE-SECRET";
    internal static ShippedSceneGuardFixture Create(string variant, Action<JsonObject>? configure = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "ke-scene-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, PrivateOracleFixtures.SecureDirectoryMode);
        try
        {
            JsonObject comparison = null!;
            var environment = PrivateOracleFixtures.SyntheticEnvironment(root, Sentinel, source => comparison = Write(source, variant), JsonSerializer.SerializeToElement("pending"));
            string provenancePath = environment["KHAOZ_R2_ORACLE_PROVENANCE"]!;
            var provenance = JsonNode.Parse(File.ReadAllText(provenancePath))!.AsObject();
            provenance["comparison"] = comparison;
            if (variant == "unknown-field") comparison["unapproved"] = Sentinel;
            if (variant == "case-field") { comparison["SchemaVersion"] = 1; comparison.Remove("schemaVersion"); }
            if (variant == "missing-field") comparison.Remove("materials");
            if (variant == "unapproved-rule") comparison["policy"]!["probes"]![0]!["roofMode"] = "Unapproved";
            string sourceRoot = environment["KHAOZ_R2_SHIPPED_SOURCE"]!;
            if (variant is "unattested-helper" or "unattested-pack")
            {
                string omitted = variant == "unattested-helper" ? "generator/helper.py" : "native-tiled/map.json";
                var paths = provenance["paths"]!.AsArray(); paths.Remove(paths.Single(p => p!["path"]!.GetValue<string>() == omitted));
                File.Delete(Path.Combine(sourceRoot, omitted));
                var typed = provenance.Deserialize<ShippedSourceProvenance>(ShippedSourceProvenance.JsonOptions)!;
                provenance["aggregateSha256"] = typed.ComputeAggregate();
            }
            configure?.Invoke(comparison);
            string text = provenance.ToJsonString(ShippedScenePolicy.Json);
            if (variant == "duplicate-field") text = text.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
            File.WriteAllText(provenancePath, text);
            environment["KHAOZ_R2_ORACLE_PROVENANCE_SHA256"] = Hash(File.ReadAllBytes(provenancePath));
            if (variant == "wrong-digest") environment["KHAOZ_R2_ORACLE_PROVENANCE_SHA256"] = new string('0', 64);
            if (variant == "stale-source") File.AppendAllText(Path.Combine(sourceRoot, "catalog.json"), Sentinel);
            string? missing = variant switch
            {
                "missing-source" => "KHAOZ_R2_SHIPPED_SOURCE",
                "missing-provenance" => "KHAOZ_R2_ORACLE_PROVENANCE",
                "missing-digest" => "KHAOZ_R2_ORACLE_PROVENANCE_SHA256",
                "missing-report" => "KHAOZ_R2_ORACLE_REPORT",
                _ => null
            };
            if (missing is not null) environment.Remove(missing);
            string report = Path.Combine(root, "out", "report.json");
            if (variant == "unsafe-report") File.SetUnixFileMode(Path.GetDirectoryName(report)!, PrivateOracleFixtures.SecureDirectoryMode | UnixFileMode.GroupRead);
            if (variant == "existing-report") File.WriteAllText(report, Sentinel);
            if (variant == "linked-report") File.CreateSymbolicLink(report, Path.Combine(root, "absent-report"));
            return new(root, environment);
        }
        catch { Directory.Delete(root, true); throw; }
    }
    static JsonObject Write(string root, string variant)
    {
        bool wideRaster = variant == "foliage-wide-raster";
        if (wideRaster) variant = "foliage-valid";
        bool roofHlod = variant.StartsWith("roof-hlod", StringComparison.Ordinal);
        bool wrongNativeTileSize = variant == "roof-hlod-native-tile-size";
        bool roofShadows = variant != "roof-hlod-no-shadows";
        bool exitOverlap = variant == "roof-hlod-exit-overlap";
        if (roofHlod) variant = "roof-valid";
        foreach (string directory in new[] { "world", "assets", "native-assets", "generator" }) Directory.CreateDirectory(Path.Combine(root, directory));
        var world = new TileWorldDocument { Id = "invented-scene-world", PlaneCount = 1 };
        world.CatalogPaths.Add("../catalog.json");
        world.GetOrCreateRegion(new(0, 0));
        int floorSize = variant == "foliage-valid" ? 64 : 4;
        for (int x = 0; x < floorSize; x++) for (int z = 0; z < floorSize; z++) world.SetUnderlay(x, z, 0, 1);
        world.AddObject("column", 2, 2, 0, 0);
        if (variant == "coincident-valid") world.AddObject("column", 2, 2, 0, 0);
        bool cover = variant == "foliage-valid", waterScene = variant == "water-valid", lightScene = variant.StartsWith("light-", StringComparison.Ordinal);
        bool featherOrigin = variant == "terrain-feather-origin", terrainPainted = featherOrigin || variant == "terrain-painted";
        if (cover) AddFoliageSource(world, wideRaster ? 33 : 4);
        if (variant.StartsWith("marker-", StringComparison.Ordinal)) world.SetMarker("arrival", 1, 1, 0, ["first", "second"]);
        if (terrainPainted)
        {
            if (!featherOrigin) world.SetOverlay(1, 1, 0, 2);
            int feather = featherOrigin ? 0 : 2;
            world.SetOverlay(feather, feather, 0, 2); world.SetSettings(feather, feather, 0, TileSettings.FeatherOverlay);
        }
        if (waterScene)
        {
            for (int x = 1; x <= 2; x++) for (int z = 1; z <= 2; z++) world.SetUnderlay(x, z, 0, 2);
            world.SetCornerHeightCm(2, 2, 0, -100);
        }
        TileWorldFile.Save(world, Path.Combine(root, "world"));
        File.WriteAllText(Path.Combine(root, "catalog.json"), """
            {"materials":[{"id":1,"name":"Invented ground","color":"#ffffff"}],
             "archetypes":[{"id":"column","name":"Invented column","meshRef":"assets/base.glb","lodMeshRef":"assets/far.glb"}]}
            """);
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "catalog.json")))!.AsObject();
        if (variant == "roof-valid") catalog["archetypes"]![0]!["isRoof"] = true;
        if (cover) catalog["archetypes"]!.AsArray().Add(JsonNode.Parse("""{"id":"reed","name":"Invented reed","meshRef":"assets/base.glb"}"""));
        if (waterScene) catalog["materials"]!.AsArray().Add(JsonNode.Parse("""{"id":2,"name":"Invented water","color":"#ffffff","kind":"Water"}"""));
        if (terrainPainted) catalog["materials"]!.AsArray().Add(JsonNode.Parse("""{"id":2,"name":"Invented paint","color":"#ffffff"}"""));
        File.WriteAllText(Path.Combine(root, "catalog.json"), catalog.ToJsonString(ShippedScenePolicy.Json));
        File.WriteAllText(Path.Combine(root, "generator/build-scaffold.py"), "# Invented public contract fixture\n");
        File.WriteAllText(Path.Combine(root, "generator/helper.py"), "# Invented public helper\n");
        byte[] mesh = TriangleGlb(); File.WriteAllBytes(Path.Combine(root, "assets/base.glb"), mesh); File.WriteAllBytes(Path.Combine(root, "assets/far.glb"), mesh);
        var resources = new JsonArray();
        var resourceRows = new List<(string Id, string Path, string Kind)> { ("base", "native-assets/base.glb", "Mesh"), ("far", "native-assets/far.glb", "Lod"), ("paint", "assets/paint.json", "Material") };
        if (waterScene) { resourceRows.Add(("water-ground", "assets/water-ground.json", "Material")); resourceRows.Add(("legacy-water", "assets/legacy-water.json", "Material")); }
        if (terrainPainted) resourceRows.Add(("paint-overlay", "assets/paint-overlay.json", "Material"));
        foreach (var (id, path, kind) in resourceRows)
        {
            if (id is "base" or "far")
            {
                byte[] original = mesh.ToArray();
                if (id == "base" && variant == "native-mesh")
                {
                    int jsonLength = BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(12));
                    BinaryPrimitives.WriteSingleLittleEndian(original.AsSpan(28 + jsonLength), .125f);
                }
                File.WriteAllBytes(Path.Combine(root, path), original);
            }
            if (kind == "Material") File.WriteAllText(Path.Combine(root, path), """
                {"schema":"kemap/render-material","payloadVersion":1,"baseColour":[1,1,1,1],"roughness":1,"metallic":0,"alphaCutoff":0,"albedo":null,"normal":null,"roughnessMap":null}
                """);
            resources.Add(NativeAssetFixtures.Resource(new(id, path, Hash(File.ReadAllBytes(Path.Combine(root, path))), 1), kind));
        }
        var asset = JsonNode.Parse("""
            {"id":"native-column","meshResourceId":"base","collisionResourceId":null,"selectionResourceId":null,
             "supportResourceIds":[],"materialResourceIds":[],"lodResourceIds":["far"],"lightResourceIds":[],"sourceUnitsToMetres":1,
             "renderBounds":{"min":{"x":0,"y":0,"z":0},"max":{"x":1,"y":1,"z":1}},
             "lodBounds":{"min":{"x":0,"y":0,"z":0},"max":{"x":1,"y":1,"z":1}},"lightBounds":null,
             "source":"invented-column.blend","license":"CC0","category":"fixtures","textured":false}
            """);
        var assets = new JsonArray(asset);
        if (cover) { var second = asset!.DeepClone(); second["id"] = "native-reed"; second["lodResourceIds"] = new JsonArray(); assets.Add(second); }
        if (lightScene) AddLightAsset(root, asset!, resources, LightExclusion(variant));
        if (variant == "roof-valid") AddRoofAsset(root, asset!, resources);
        byte[] manifest = Encoding.UTF8.GetBytes(new JsonObject { ["payloadVersion"] = 1, ["assets"] = assets, ["resources"] = resources }.ToJsonString(ShippedScenePolicy.Json));
        File.WriteAllBytes(Path.Combine(root, "assets/root.json"), manifest);
        var native = MapDocumentFile.LoadText("""
            {"formatVersion":9,"id":"invented-native-scene","tileSize":16,"bounds":{"minX":0,"minZ":-64,"maxX":64,"maxZ":0},
             "playableBounds":{"minX":0,"minZ":-64,"maxX":64,"maxZ":0},"terrain":{},
             "resolverIdentity":{"payloadVersion":1,"resolverVersion":2},"supportRecipe":"AuthoredBindingsV2"}
            """);
        native.NativeAssets.Add(new("root", "assets/root.json", Hash(manifest), 1));
        var converted = LegacyOracleConverter.ToNative(world, new(0, 0), 0);
        native.Surfaces.Refs.Add(converted.Surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(converted.Surface,
            [KeyValuePair.Create(converted.Patch.Key, MapSurfaceSemantics.PatchDigest(converted.Patch))])
        });
        native.Surfaces.Patches.Add(converted.Patch.Key, converted.Patch);
        native.NumericIdHighWaterMark = 1;
        native.Placements.Add(new() { Id = "native-placement", AssetId = "native-column", Kind = "column", NumericId = 1, X = variant == "native-transform" ? 3 : 2.5f, Y = world.HeightAt(2.5f, -2.5f, 0), Z = -2.5f });
        if (variant == "coincident-valid")
        {
            native.NumericIdHighWaterMark = 2;
            native.Placements.Add(new() { Id = "native-coincident", AssetId = "native-column", Kind = "column", NumericId = 2, X = 2.5f, Y = 0, Z = -2.5f });
        }
        if (variant == "roof-valid")
        {
            native.Prefabs = new(1, 1, [], []);
            native.BuildingIntent = new(1, [], [], [], []) { Roofs = [new(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, "native-placement")] };
            WriteRoofManifest(root);
        }
        if (waterScene)
        {
            var convertedWater = LegacyWaterConverter.ToNative(world, TileWorldCatalogs.LoadJson(catalog.ToJsonString(ShippedScenePolicy.Json), "invented scene catalog"));
            native.Surfaces = convertedWater.Document.Surfaces; native.Water = convertedWater.Document.Water;
            foreach (var patch in native.Surfaces.Patches.Values)
                for (int r = 0; r < patch.Records.Count; r++)
                    if (patch.Records[r] is KhaozEngine.MapDoc.Spaces.MapSpaceFootprint footprint)
                        patch.Records[r] = footprint with { SlotCells = new[] { 65, 66, 129, 130 }, Lower = new(KhaozEngine.MapDoc.Spaces.MapBoundKind.SupportFloor, "plane-0", null) };
            foreach (var patch in native.Surfaces.Patches.Values)
            {
                var heights = new int[25]; var cells = new MapSurfaceCell[16];
                for (int z = 0; z <= 4; z++) for (int x = 0; x <= 4; x++) heights[z * 5 + x] = patch.Heights[z * 65 + x];
                for (int z = 0; z < 4; z++) for (int x = 0; x < 4; x++) cells[z * 4 + x] = patch.Cells[z * 64 + x];
                patch.Width = 4; patch.Depth = 4; patch.Heights = heights; patch.Cells = cells; patch.Presence = [65535];
            }
            for (int i = 0; i < native.Surfaces.Refs.Count; i++)
            {
                var surface = native.Surfaces.Refs[i];
                native.Surfaces.Refs[i] = surface with
                {
                    SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                    native.Surfaces.Patches.Where(p => p.Key.SurfaceId == surface.Id).Select(p => KeyValuePair.Create(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))))
                };
            }
        }
        if (cover) AddFoliageNative(native, root, wideRaster ? 33 : 4);
        if (variant.StartsWith("marker-", StringComparison.Ordinal)) WriteSceneMarkers(root, native, variant);
        File.WriteAllText(Path.Combine(root, "native.json"), MapDocumentFile.SaveText(native)); MapDocumentFile.SaveTiled(native, Path.Combine(root, "native-tiled"));
        var foliage = new GroundCoverRenderOptions { UseGpuBatches = true, FadeMode = GroundCoverFadeMode.HeightScale };
        JsonObject Policy(string assetId) => new()
        {
            ["placements"] = JsonSerializer.SerializeToNode(new MapPlacementPolicy(variant is "roof-valid" or "animated-valid" ? [] : [new("ordinary", new HashSet<string> { assetId }, new float[] { 10 }, 4, 100, 0, 0, 0, true)], 100), ShippedScenePolicy.Json),
            ["animatedAssets"] = variant == "animated-valid" ? new JsonArray(assetId) : new JsonArray(),
            ["foliage"] = JsonSerializer.SerializeToNode(cover ? CoverOptions(assetId == "native-column") : foliage, ShippedScenePolicy.Json),
            ["water"] = JsonSerializer.SerializeToNode(new WaterLook { WaveSource = WaterWaveSource.Procedural, SwellAmplitude = 0 }, ShippedScenePolicy.Json),
            ["lights"] = lightScene ? SourceLights(variant) : new JsonArray(),
            ["modelExtents"] = cover ? Extents(assetId == "native-column") : variant == "animated-valid" ? ExplicitExtent(assetId) : new JsonArray()
        };
        File.WriteAllText(Path.Combine(root, "source-policy.json"), Policy("column").ToJsonString(ShippedScenePolicy.Json));
        File.WriteAllText(Path.Combine(root, "native-policy.json"), Policy("native-column").ToJsonString(ShippedScenePolicy.Json));
        JsonObject Window(string id) => new()
        {
            ["id"] = id,
            ["minX"] = id == "A" ? -1 : id == "B" ? -.5 : -.25,
            ["minY"] = -10,
            ["minZ"] = -5,
            ["maxX"] = id == "A" ? 5 : id == "B" ? 5.5 : 6,
            ["maxY"] = 10,
            ["maxZ"] = 1,
            ["spaceIds"] = null,
            ["motionPaddingMetres"] = waterScene ? 1 : 0
        };
        var comparison = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["generator"] = new JsonObject { ["path"] = "generator/build-scaffold.py", ["helpers"] = new JsonArray("generator/helper.py") },
            ["nativeDocumentPath"] = "native.json",
            ["nativeTiledManifestPath"] = "native-tiled/map.json",
            ["buildingManifestPath"] = null,
            ["markerManifestPath"] = null,
            ["foliageManifestPath"] = null,
            ["assetSelections"] = new JsonArray(SelectionCorrespondence("column", "native-column")),
            ["materials"] = new JsonArray(new JsonObject { ["sourceMaterialId"] = 1, ["nativeResourceId"] = "paint" }),
            ["placements"] = new JsonArray(new JsonObject
            {
                ["sourceObjectId"] = "1",
                ["nativePlacementId"] = "native-placement",
                ["lods"] = new JsonArray(
                new JsonObject { ["sourcePath"] = "assets/base.glb", ["nativeResourceId"] = "base" }, new JsonObject { ["sourcePath"] = "assets/far.glb", ["nativeResourceId"] = "far" })
            }),
            ["lights"] = new JsonArray(),
            ["roofs"] = new JsonArray(),
            ["water"] = new JsonArray(),
            ["foliage"] = new JsonArray(),
            ["windows"] = new JsonArray(Window("A"), Window("B"), Window("C")),
            ["sequence"] = new JsonArray(new JsonObject { ["operation"] = "Load", ["windowId"] = "A" }, new JsonObject { ["operation"] = "Load", ["windowId"] = "B" },
                new JsonObject { ["operation"] = "Unload", ["windowId"] = "A" }, new JsonObject { ["operation"] = "Recenter", ["windowId"] = "C" },
                new JsonObject { ["operation"] = "Unload", ["windowId"] = "B" }, new JsonObject { ["operation"] = "Unload", ["windowId"] = "C" }),
            ["policy"] = new JsonObject
            {
                ["sourcePolicyPath"] = "source-policy.json",
                ["nativePolicyPath"] = "native-policy.json",
                ["probes"] = new JsonArray(new JsonObject
                { ["id"] = "near", ["windowId"] = "A", ["observerX"] = 0, ["observerY"] = 2, ["observerZ"] = 0, ["roofMode"] = "AlwaysVisible", ["timeSeconds"] = 1, ["windSeed"] = 17, ["focusX"] = 0, ["focusY"] = 2, ["focusZ"] = 0 },
                new JsonObject
                {
                    ["id"] = "midpoint",
                    ["windowId"] = "A",
                    ["observerX"] = 0,
                    ["observerY"] = 2,
                    ["observerZ"] = 0,
                    ["roofMode"] = "AlwaysVisible",
                    ["timeSeconds"] = 1,
                    ["windSeed"] = 17,
                    ["focusX"] = 12.5,
                    ["focusY"] = 2,
                    ["focusZ"] = -2.5
                },
                new JsonObject
                {
                    ["id"] = "default-focus",
                    ["windowId"] = "A",
                    ["observerX"] = 0,
                    ["observerY"] = 2,
                    ["observerZ"] = 0,
                    ["roofMode"] = "AlwaysVisible",
                    ["timeSeconds"] = 1,
                    ["windSeed"] = 17,
                    ["focusX"] = null,
                    ["focusY"] = null,
                    ["focusZ"] = null
                })
            },
            ["expectations"] = new JsonObject { ["terrainFaces"] = 2 * floorSize * floorSize, ["placements"] = 1, ["parts"] = 2, ["lods"] = 2, ["lights"] = 0, ["roofs"] = 0, ["waterRegions"] = 0, ["foliageSamples"] = 0, ["windows"] = 3, ["sequenceSteps"] = 6 }
        };
        if (cover)
        {
            comparison["foliageManifestPath"] = "foliage.json";
            comparison["foliage"] = new JsonArray(new JsonObject { ["sourceLayerId"] = "source-cover", ["nativeLayerId"] = "native-cover" });
            var sourceCatalog = TileWorldCatalogs.LoadJson(catalog.ToJsonString(ShippedScenePolicy.Json), "invented scene catalog");
            long count = world.FoliageLayers.Sum(l => GroundCoverDistribution.Generate(new RectArea(0, -64, 64, 0),
                ShippedFoliageComparison.Settings(l), new TileFoliageSurface(world, sourceCatalog, l).Sample).Count);
            comparison["expectations"]!["foliageSamples"] = count;
        }
        if (variant.StartsWith("marker-", StringComparison.Ordinal)) comparison["markerManifestPath"] = "scene-marker-comparison.json";
        if (waterScene)
        {
            comparison["materials"]!.AsArray().Add(new JsonObject { ["sourceMaterialId"] = 2, ["nativeResourceId"] = "water-ground" });
            comparison["water"] = new JsonArray(new JsonObject { ["sourceBodyKey"] = "legacy-water/0/0/0/0", ["nativeBodyId"] = "legacy-water/0/0/0/0", ["nativeSpaceIds"] = new JsonArray(native.Water!.Bodies[0].Covers[0].Id) });
            comparison["expectations"]!["waterRegions"] = 1;
        }
        if (lightScene)
        {
            comparison["lights"] = new JsonArray(new JsonObject { ["sourceObjectId"] = "1", ["sourceProfileId"] = "bulb", ["nativeLightId"] = "native-placement/bulb/bulb" });
            comparison["expectations"]!["lights"] = 1;
        }
        if (terrainPainted) comparison["materials"]!.AsArray().Add(new JsonObject { ["sourceMaterialId"] = 2, ["nativeResourceId"] = "paint-overlay" });
        if (variant == "coincident-valid")
        {
            var second = comparison["placements"]![0]!.DeepClone(); second["sourceObjectId"] = "2"; second["nativePlacementId"] = "native-coincident";
            comparison["placements"]!.AsArray().Add(second);
            comparison["expectations"]!["placements"] = 2; comparison["expectations"]!["parts"] = 4; comparison["expectations"]!["lods"] = 4;
        }
        if (variant == "roof-valid")
        {
            comparison["buildingManifestPath"] = "building.json";
            comparison["roofs"] = new JsonArray(new JsonObject { ["sourceObjectId"] = "1", ["nativePlacementId"] = "native-placement" });
            comparison["expectations"]!["roofs"] = 1;
            foreach (var probe in comparison["policy"]!["probes"]!.AsArray()) probe!["roofMode"] = "AlwaysHidden";
        }
        if (roofHlod) AddRoofHlod(root, world, native, comparison, roofShadows, exitOverlap, wrongNativeTileSize ? 512 : 1);
        return WithStorageAndSelections(root, native, comparison, variant);
    }
    internal static byte[] TriangleGlb()
    {
        byte[] buffer = new byte[44];
        float[] values = [0, 0, 0, 1, 0, 0, 0, 1, 1];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(i * 4), values[i]);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(36), 0); BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(38), 1); BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(40), 2);
        byte[] json = Encoding.UTF8.GetBytes("""
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":44}],"bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},{"buffer":0,"byteOffset":36,"byteLength":6}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,1]},
             {"bufferView":1,"componentType":5123,"count":3,"type":"SCALAR"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1}]}],
             "nodes":[{"mesh":0}],"scenes":[{"nodes":[0]}],"scene":0}
            """);
        int length = (json.Length + 3) & ~3;
        byte[] glb = new byte[12 + 8 + length + 8 + buffer.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(glb, 0x46546C67); BinaryPrimitives.WriteInt32LittleEndian(glb.AsSpan(4), 2); BinaryPrimitives.WriteInt32LittleEndian(glb.AsSpan(8), glb.Length);
        BinaryPrimitives.WriteInt32LittleEndian(glb.AsSpan(12), length); BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4E4F534A);
        glb.AsSpan(20, length).Fill(32); json.CopyTo(glb, 20);
        BinaryPrimitives.WriteInt32LittleEndian(glb.AsSpan(20 + length), buffer.Length); BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(24 + length), 0x004E4942); buffer.CopyTo(glb, 28 + length);
        return glb;
    }
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

internal sealed class SceneOracleScheduler : TaskScheduler
{
    readonly Queue<Task> _tasks = new();
    internal Exception? LastFailure { get; private set; }
    protected override IEnumerable<Task> GetScheduledTasks() => _tasks.ToArray();
    protected override void QueueTask(Task task) => _tasks.Enqueue(task);
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    internal void RunAll()
    {
        while (_tasks.TryDequeue(out var task))
        {
            TryExecuteTask(task);
            if (task.Exception is { } error) LastFailure = error.Flatten().InnerExceptions[0];
        }
    }
}
internal sealed class SceneOracleDevice : IMapRenderDevice
{
    readonly Dictionary<MapRenderHandle, ISceneUploadReservation> _leases = new();
    long _next;
    internal Dictionary<MapRenderHandle, MapPreparedResource> Live { get; } = new();
    public SceneUploadDemand MeasureUpload(MapPreparedResource resource) => new(resource.PayloadDigest, 128, 16, 128, 128, 32);
    public IMapRenderUpload BeginUpload(MapPreparedResource resource, ISceneUploadReservation reservation)
    {
        reservation.Claim(MeasureUpload(resource));
        // Upload owns its rows after the admitted borrow ends, just as the real render device does.
        if (resource.Payload is MapFoliageResource foliage)
            resource = resource with
            {
                Payload = foliage.TransformSpace == MapFoliageTransformSpace.World
                ? new MapFoliageResource(foliage.Anchor, foliage.WorldTransforms, foliage.Ranks, foliage.ModelIndices, foliage.Models)
                : MapFoliageResource.FromAnchorLocal(foliage.Anchor, foliage.AnchorLocalTransforms, foliage.Ranks, foliage.ModelIndices, foliage.Models)
            };
        return new Upload(this, resource, reservation);
    }
    public void Release(MapRenderHandle handle)
    {
        if (!Live.Remove(handle)) throw new InvalidOperationException("retired scene handle");
        var lease = _leases[handle]; _leases.Remove(handle); lease.Retiring(); lease.Released();
    }
    sealed class Upload(SceneOracleDevice owner, MapPreparedResource resource, ISceneUploadReservation lease) : IMapRenderUpload
    {
        long _remaining = 32; bool _published;
        public bool Ready => _remaining == 0;
        public long Advance(long transferBytes) { long n = Math.Min(transferBytes, _remaining); _remaining -= n; return n; }
        public MapRenderHandle Publish()
        {
            if (!Ready || _published) throw new InvalidOperationException("scene upload is not publishable");
            _published = true; var handle = new MapRenderHandle(++owner._next); lease.Published(new(handle.Value));
            owner.Live.Add(handle, resource); owner._leases.Add(handle, lease); return handle;
        }
        public void Dispose() { if (!_published) { lease.Retiring(); lease.Released(); _published = true; } }
    }
}
