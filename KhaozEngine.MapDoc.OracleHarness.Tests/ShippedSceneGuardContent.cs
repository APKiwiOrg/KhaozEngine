using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
internal static partial class ShippedSceneGuardFixtures
{
    static void AddFoliageSource(TileWorldDocument world, int width = 4) => world.SetFoliageLayer(new TileFoliageLayer("source-cover", 0, 0, -4, 1, width, 4,
        Enumerable.Range(0, width * 4).Select(i => i % width < 4 ? (byte)255 : (byte)0).ToArray(), 23, 1, 1, 1, 0,
        [new TileFoliageArchetype("column", 1), new TileFoliageArchetype("reed", 1)], [1],
        excludeIndoors: false, excludeSolidObjects: false, doorClearance: 0, edgeFade: 0));
    static void AddFoliageNative(MapDocument native, string root, int width = 4)
    {
        var density = new byte[1024];
        for (int z = 0; z < 4; z++) for (int x = 0; x < 4; x++) density[z * 32 + x] = 255;
        native.Foliage = new(1, [new(1, "native-cover", new(MapSupportBindingKind.Surface, "plane-0", null, null, null, null),
            0, -4, 1, width, 4, MapRowDirection.PositiveZ, 23, 1, 1, 1, 0, [new("native-column", 1), new("native-reed", 1)], [1],
            [new(1, "paint", MapFoliageMaterialKind.Ordinary)], new("legacy-tile", 1, 0), false, false, 0, 0, [new("plane-0", 0)], [], [], [], [new(new(0, 0), density)])]);
        var manifest = JsonNode.Parse("""
            {"schemaVersion":1,"generator":{"path":"generator/build-scaffold.py","helpers":["generator/helper.py"]},
             "layers":[{"sourceLayerId":"source-cover","nativeLayerId":"native-cover","support":{"kind":"Surface","surfaceId":"plane-0"},
             "compatibilitySupports":[{"sourcePlane":0,"nativeSurfaceId":"plane-0"}],
             "models":[{"sourceArchetypeId":"column","nativeAssetId":"native-column"},{"sourceArchetypeId":"reed","nativeAssetId":"native-reed"}],
             "materials":[{"sourceMaterialId":1,"nativeResourceId":"paint","kind":"Ordinary"}],"ruleIds":[],"maskIds":[],"ownerIds":[],"candidateConvention":"world-lattice-v1"}],"owners":[]}
            """)!;
        File.WriteAllText(Path.Combine(root, "foliage.json"), manifest.ToJsonString(ShippedScenePolicy.Json));
    }
    static GroundCoverRenderOptions CoverOptions(bool native) => new()
    {
        UseGpuBatches = true,
        FadeMode = GroundCoverFadeMode.HeightScale,
        WindDirection = new(.25f, .75f),
        WindStrength = .2f,
        WindSpeed = 2,
        WindSpatialFrequency = .1f,
        Interactors = [new(new(1, 1, -1), 2, .5f, .1f)],
        MotionProfiles = new Dictionary<string, FoliageMotionProfile> { [native ? "native-reed" : "reed"] = new(1, .2f, 2, .1f) },
        ModelCastsShadows = new Dictionary<string, bool> { [native ? "native-reed" : "reed"] = false }
    };
    static JsonNode Extents(bool native) => JsonSerializer.SerializeToNode(new[]
    {
        new MapFoliageModelExtent(native ? "native-column" : "column", new(Vector3.Zero, Vector3.One), 1, 0),
        new MapFoliageModelExtent(native ? "native-reed" : "reed", new(Vector3.Zero, Vector3.One), 1, 0)
    }, ShippedScenePolicy.Json)!;
    static string? LightExclusion(string variant) => variant switch
    {
        "light-none" => """{"kind":"None"}""",
        "light-sphere" => """{"kind":"NearSphere","radius":0.625}""",
        "light-box" => """{"kind":"FixtureBox","min":{"x":-0.25,"y":-0.5,"z":-0.75},"max":{"x":0.5,"y":0.75,"z":1},"margin":0.125}""",
        _ => null
    };
    static JsonNode SourceLights(string variant)
    {
        var rows = JsonSerializer.SerializeToNode(new[] { new SceneSourceLight(1, "bulb", new(.25f, .5f, 0), new(1, .5f, .25f), 2, 3, true) }, ShippedScenePolicy.Json)!;
        if (LightExclusion(variant) is { } declaration) rows[0]!["shadowExclusion"] = JsonNode.Parse(declaration);
        return rows;
    }
    static void AddLightAsset(string root, JsonNode asset, JsonArray resources, string? exclusion)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("""
            {"resourceSchema":"kemap/light-resource","payloadVersion":1,"id":"bulb","owner":{"scope":"Local","kind":"Placement","id":"$owner"},
             "position":{"x":0.25,"y":0.5,"z":0},"colour":{"x":1,"y":0.5,"z":0.25},"intensity":2,"radius":3,"castShadows":true}
            """);
        if (exclusion is not null)
        {
            var json = JsonNode.Parse(bytes)!; json["shadowExclusion"] = JsonNode.Parse(exclusion);
            bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        }
        File.WriteAllBytes(Path.Combine(root, "assets/bulb.json"), bytes);
        resources.Add(NativeAssetFixtures.Resource(new MapAssetRef("bulb", "assets/bulb.json", Hash(bytes), 1), "Light"));
        asset["lightResourceIds"] = new JsonArray("bulb");
        asset["lightBounds"] = JsonNode.Parse("""{"min":{"x":-2.75,"y":-2.5,"z":-3},"max":{"x":3.25,"y":3.5,"z":3}}""");
    }
}
