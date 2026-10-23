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
        if (variant == "monolithic-native-metadata-mismatch")
        {
            native.NumericIdHighWaterMark++;
            MapDocumentFile.Save(native, Path.Combine(root, "native.json"));
        }
        if (variant == "monolithic-surface-mismatch")
        {
            native.Surfaces.Patches.First().Value.Heights[0] += 1;
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
        if (variant == "foliage-valid")
            comparison["assetSelections"]!.AsArray().Add(SelectionCorrespondence("reed", "native-reed"));
        if (variant.StartsWith("unplaced-selection", System.StringComparison.Ordinal))
        {
            string catalogPath = Path.Combine(root, "catalog.json");
            var catalog = JsonNode.Parse(File.ReadAllText(catalogPath))!;
            var archetype = catalog["archetypes"]![0]!.DeepClone();
            archetype["id"] = "unused-column";
            archetype["meshRef"] = "assets/unplaced.glb";
            archetype["lodMeshRef"] = "assets/unplaced-far.glb";
            catalog["archetypes"]!.AsArray().Add(archetype);
            File.WriteAllText(catalogPath, catalog.ToJsonString(ShippedScenePolicy.Json));
            File.Copy(Path.Combine(root, "assets/base.glb"), Path.Combine(root, "assets/unplaced.glb"));
            File.Copy(Path.Combine(root, "assets/far.glb"), Path.Combine(root, "assets/unplaced-far.glb"));
            string assetPath = Path.Combine(root, "assets/root.json");
            var assets = JsonNode.Parse(File.ReadAllText(assetPath))!;
            var asset = assets["assets"]![0]!.DeepClone(); asset["id"] = "native-unused-column";
            assets["assets"]!.AsArray().Add(asset);
            if (variant == "unplaced-selection-wrong-policy")
            {
                var other = asset.DeepClone(); other["id"] = "native-other-column";
                assets["assets"]!.AsArray().Add(other);
            }
            File.WriteAllText(assetPath, assets.ToJsonString(ShippedScenePolicy.Json));
            native.NativeAssets[0] = native.NativeAssets[0] with { Sha256 = Hash(File.ReadAllBytes(assetPath)) };
            MapDocumentFile.Save(native, Path.Combine(root, "native.json"));
            MapDocumentFile.SaveTiled(native, Path.Combine(root, "native-tiled"));
            foreach (string name in new[] { "source-policy.json", "native-policy.json" })
            {
                string path = Path.Combine(root, name);
                var policy = JsonNode.Parse(File.ReadAllText(path))!;
                var layer = policy["placements"]!["layers"]![0]!.DeepClone();
                layer["id"] = "unplaced";
                layer["assetIds"] = new JsonArray(name == "source-policy.json" ? "unused-column" : "native-unused-column");
                if (name == "native-policy.json" && variant == "unplaced-selection-wrong-policy")
                    layer["assetIds"] = new JsonArray("native-other-column");
                layer["hlodDistance"] = 20; layer["hlodCrossfadeWidth"] = 4; layer["hlodWeldCell"] = 1;
                policy["placements"]!["layers"]!.AsArray().Add(layer);
                File.WriteAllText(path, policy.ToJsonString(ShippedScenePolicy.Json));
            }
            var selection = SelectionCorrespondence("unused-column", "native-unused-column");
            comparison["assetSelections"]!.AsArray().Add(selection);
            if (variant == "unplaced-selection-missing-mapping") comparison["assetSelections"]!.AsArray().Remove(selection);
            if (variant == "unplaced-selection-unknown-source") selection["sourceArchetypeId"] = "unknown-column";
            if (variant == "unplaced-selection-unknown-native") selection["nativeAssetIds"] = new JsonArray("unknown-column");
            if (variant == "unplaced-selection-duplicate-source") comparison["assetSelections"]!.AsArray().Add(selection.DeepClone());
            if (variant == "unplaced-selection-observed-mismatch")
                comparison["assetSelections"]![0]!["nativeAssetIds"] = new JsonArray("native-unused-column");
        }
        return comparison;
    }

    static JsonObject SelectionCorrespondence(string source, string native) => new()
    {
        ["sourceArchetypeId"] = source,
        ["nativeAssetIds"] = new JsonArray(native),
    };
}
