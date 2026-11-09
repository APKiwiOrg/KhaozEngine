using System.IO;
using System.Numerics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.Physics;
using KhaozEngine.Tests.MapDoc;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
internal static partial class ShippedSceneGuardFixtures
{
    static JsonNode ExplicitExtent(string id) => JsonSerializer.SerializeToNode(new[]
    { new MapFoliageModelExtent(id, new(Vector3.Zero, Vector3.One), 1, 0) }, ShippedScenePolicy.Json)!;

    static void AddRoofAsset(string root, JsonNode asset, JsonArray resources)
    {
        using var stream = new MemoryStream();
        PropCollisionFormat.Write(new CompoundShape([new(new BoxShape(new(.5f)), new Pose(new(.5f), Quaternion.Identity))]), stream);
        byte[] bytes = stream.ToArray(); File.WriteAllBytes(Path.Combine(root, "assets/selection.json"), bytes);
        resources.Add(NativeAssetFixtures.Resource(new MapAssetRef("selection", "assets/selection.json", Hash(bytes), 1), "Selection"));
        asset["selectionResourceId"] = "selection";
    }

    static void WriteRoofManifest(string root) => File.WriteAllText(Path.Combine(root, "building.json"), JsonNode.Parse("""
        {"schemaVersion":1,"nativeDocumentPath":"native.json",
         "sourceCollisionOptions":{"wallThickness":0.1,"blockedHeight":3,"walkSurfaceThickness":0.1},
         "expectations":{"definitions":0,"buildings":0,"originalLeaves":1,"connectedInteriors":0,"roofs":1,"openings":0,"doorwayRecords":0,"dedicatedLights":0},
         "definitions":[],"selectedLeafIds":[1],"buildings":[],
         "leaves":[{"sourceId":1,"buildingId":null,"key":"native-placement","worldId":"native-placement","assetId":"native-column",
          "meshPath":"assets/base.glb","selectionPath":"assets/selection.json",
          "metadata":{"source":"invented-column.blend","license":"CC0","category":"fixtures","textured":false,"sourceUnitsToMetres":1,
           "renderBounds":{"min":[0,0,0],"max":[1,1,1]},"lodBounds":{"min":[0,0,0],"max":[1,1,1]},"lightBounds":null}}],
         "floors":[],"rooms":[],"groups":[],"roofs":[{"leafId":1,"linkId":null,"rooms":[],"groups":[],"aboveY":0}],
         "lights":[],"doorwayRecords":[],"doorways":[],"probes":[]}
        """)!.ToJsonString(ShippedScenePolicy.Json));
}
