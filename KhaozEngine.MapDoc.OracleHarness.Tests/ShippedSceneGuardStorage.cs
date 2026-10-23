using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;

namespace KhaozEngine.Tests.MapDocOracle;

internal static partial class ShippedSceneGuardFixtures
{
    static JsonObject WithStorageAndSelections(string root, MapDocument native, JsonObject comparison, string variant)
    {
        if (variant == "tiled-only")
        {
            comparison["nativeDocumentPath"] = null;
            File.Delete(Path.Combine(root, "native.json"));
        }
        if (variant == "monolithic-mismatch")
        {
            native.Placements[0].X += .125f;
            MapDocumentFile.Save(native, Path.Combine(root, "native.json"));
        }
        if (variant == "missing-tiled") comparison.Remove("nativeTiledManifestPath");
        if (variant == "null-tiled") comparison["nativeTiledManifestPath"] = null;
        if (variant == "corrupt-tiled")
        {
            string path = Directory.GetFiles(Path.Combine(root, "native-tiled", "tiles"), "*.json", SearchOption.AllDirectories)
                .Single(p => JsonNode.Parse(File.ReadAllText(p)) is JsonObject json && json["placements"] is JsonArray);
            var tile = JsonNode.Parse(File.ReadAllText(path))!;
            tile["placements"]![0]!["x"] = 3;
            File.WriteAllText(path, tile.ToJsonString(ShippedScenePolicy.Json));
        }
        return comparison;
    }
}
