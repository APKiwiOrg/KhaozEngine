using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.MapDocOracle;

internal static partial class ShippedSceneGuardFixtures
{
    static void AddRoofHlod(string root, TileWorldDocument world, MapDocument native, JsonObject comparison, bool shadows, bool exitOverlap = false, float nativeTileSize = 1)
    {
        world.AddObject("column", 10, 2, 0, 0); TileWorldFile.Save(world, Path.Combine(root, "world"));
        native.TileSize = nativeTileSize; native.TerrainOverrides = new(.03125f); native.NumericIdHighWaterMark = 2;
        native.Placements.Add(new() { Id = "native-second", AssetId = "native-column", Kind = "column", NumericId = 2, X = 10.5f, Y = 0, Z = -2.5f });
        native.BuildingIntent = new(1, [], [], [], [])
        {
            Roofs = [new(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, "native-placement"),
            new(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, "native-second")]
        };
        File.WriteAllText(Path.Combine(root, "native.json"), MapDocumentFile.SaveText(native));
        MapDocumentFile.SaveTiled(native, Path.Combine(root, "native-tiled"));
        string buildingPath = Path.Combine(root, "building.json");
        var building = JsonNode.Parse(File.ReadAllText(buildingPath))!;
        building["expectations"]!["originalLeaves"] = 2; building["expectations"]!["roofs"] = 2;
        building["selectedLeafIds"]!.AsArray().Add(2);
        var leaf = building["leaves"]![0]!.DeepClone(); leaf["sourceId"] = 2; leaf["worldId"] = "native-second"; leaf["key"] = "native-second";
        building["leaves"]!.AsArray().Add(leaf);
        var roof = building["roofs"]![0]!.DeepClone(); roof["leafId"] = 2; building["roofs"]!.AsArray().Add(roof);
        File.WriteAllText(buildingPath, building.ToJsonString(ShippedScenePolicy.Json));
        var placement = comparison["placements"]![0]!.DeepClone(); placement["sourceObjectId"] = "2"; placement["nativePlacementId"] = "native-second";
        comparison["placements"]!.AsArray().Add(placement);
        comparison["roofs"]!.AsArray().Add(new JsonObject { ["sourceObjectId"] = "2", ["nativePlacementId"] = "native-second" });
        comparison["expectations"]!["placements"] = 2; comparison["expectations"]!["parts"] = 4;
        comparison["expectations"]!["lods"] = 4; comparison["expectations"]!["roofs"] = 2;
        foreach (string policyName in new[] { "source-policy.json", "native-policy.json" })
        {
            string path = Path.Combine(root, policyName); var policy = JsonNode.Parse(File.ReadAllText(path))!;
            string id = policyName == "source-policy.json" ? "column" : "native-column";
            policy["placements"] = JsonSerializer.SerializeToNode(new MapPlacementPolicy([new("caps", new HashSet<string> { id }, [10], 4, 100, exitOverlap ? 95 : 30, 10, 0, shadows)], 100), ShippedScenePolicy.Json);
            File.WriteAllText(path, policy.ToJsonString(ShippedScenePolicy.Json));
        }
        var windows = comparison["windows"]!.AsArray();
        windows[0]!["minX"] = 2; windows[0]!["maxX"] = 4;
        windows[1]!["minX"] = 10; windows[1]!["maxX"] = 12;
        windows[2]!["minX"] = 0; windows[2]!["maxX"] = 16;
        var probes = new JsonArray();
        foreach (float distance in new[] { 0f, 30f, 40f, 100f })
            foreach (string mode in new[] { "AlwaysVisible", "AlwaysHidden" })
                probes.Add(new JsonObject
                {
                    ["id"] = mode + distance,
                    ["windowId"] = "A",
                    ["observerX"] = 0,
                    ["observerY"] = 2,
                    ["observerZ"] = 0,
                    ["roofMode"] = mode,
                    ["timeSeconds"] = 1,
                    ["windSeed"] = 17,
                    ["focusX"] = 32 + distance,
                    ["focusY"] = 2,
                    ["focusZ"] = -32
                });
        comparison["policy"]!["probes"] = probes;
    }
}
