using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.Tests.MapDoc;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>The invented native closure: owner and model assets sharing one mesh, collider, selection and material, plus
/// the five visible ground materials the layers name. Every resource is a file under assets.</summary>
[UnsupportedOSPlatform("windows")]
internal static class ShippedFoliageGuardAssets
{
    internal static MapAssetRef Write(string root)
    {
        MapAssetRef Add(string id, byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(root, "assets", id), bytes);
            return new(id, "assets/" + id, System.Convert.ToHexStringLower(SHA256.HashData(bytes)), 1);
        }
        // An invented 0.1 m prop in the released KECL payload 1 format.
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        { writer.Write(0x4B45434Cu); writer.Write((byte)1); writer.Write((byte)4); writer.Write(0.05f); writer.Write(0.05f); writer.Write(0.05f); }
        var resources = new JsonArray(
            NativeAssetFixtures.Resource(Add("mesh", Encoding.UTF8.GetBytes("invented foliage mesh")), "Mesh", "material"),
            NativeAssetFixtures.Resource(Add("collider", stream.ToArray()), "Collider"),
            NativeAssetFixtures.Resource(Add("selection", stream.ToArray()), "Selection"),
            NativeAssetFixtures.Resource(Add("material", Encoding.UTF8.GetBytes("invented asset material")), "Material"));
        foreach (string id in new[] { "grass-material", "dirt-material", "rock-material", "water-material", "road-material" })
            resources.Add(NativeAssetFixtures.Resource(Add(id, Encoding.UTF8.GetBytes("invented " + id)), "Material"));
        var assets = new JsonArray();
        foreach (string id in new[] { "native-tree", "native-canopy", "native-roofslab", "native-doorway", "native-reed-a", "native-reed-b" })
        {
            JsonObject asset = NativeAssetFixtures.Asset();
            asset["id"] = id;
            asset["supportResourceIds"] = new JsonArray();
            asset["lightResourceIds"] = new JsonArray();
            asset["lodResourceIds"] = new JsonArray();
            asset["sourceUnitsToMetres"] = 1;
            asset["renderBounds"] = JsonNode.Parse("""{"min":{"x":-0.05,"y":-0.05,"z":-0.05},"max":{"x":0.05,"y":0.05,"z":0.05}}""");
            asset.Remove("lodBounds");
            asset.Remove("lightBounds");
            assets.Add(asset);
        }
        return Add("foliage-root", Encoding.UTF8.GetBytes(new JsonObject
        {
            ["payloadVersion"] = 1,
            ["assets"] = assets,
            ["resources"] = resources,
        }.ToJsonString()));
    }
}
