using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace KhaozEngine.MapDoc.Render3D;

public sealed record MapRenderMaterialDefinition(Vector4 BaseColour, float Roughness, float Metallic, float AlphaCutoff,
    string? Albedo, string? Normal, string? RoughnessMap);

/// <summary>The closed render-material/1 decoder. Resource IDs are resolved only against a verified closure.</summary>
public static class MapRenderMaterialJson
{
    static readonly string[] Members = ["schema", "payloadVersion", "baseColour", "roughness", "metallic", "alphaCutoff", "albedo", "normal", "roughnessMap"];
    public static MapRenderMaterialDefinition Read(ReadOnlyMemory<byte> bytes, string resourceId)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            JsonElement root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new MapRenderAssetRefusal(MapRenderAssetStatus.Unsupported, "Required native material is opaque: " + resourceId);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name) || !Members.Contains(property.Name, StringComparer.Ordinal)) throw Invalid("Unknown or duplicate material member.");
            if (names.Count != Members.Length) throw Invalid("Material is missing a required member.");
            if (root.GetProperty("schema").GetString() != "kemap/render-material" || root.GetProperty("payloadVersion").GetInt32() != 1)
                throw new MapRenderAssetRefusal(MapRenderAssetStatus.Unsupported, "Unsupported native render material version or schema.");
            var colour = root.GetProperty("baseColour");
            if (colour.ValueKind != JsonValueKind.Array || colour.GetArrayLength() != 4) throw Invalid("Material baseColour requires four components.");
            return new(new(Unit(colour[0]), Unit(colour[1]), Unit(colour[2]), Unit(colour[3])),
                Unit(root.GetProperty("roughness")), Unit(root.GetProperty("metallic")), Unit(root.GetProperty("alphaCutoff")),
                Id(root.GetProperty("albedo")), Id(root.GetProperty("normal")), Id(root.GetProperty("roughnessMap")));
        }
        catch (JsonException ex) { throw new MapRenderAssetRefusal(MapRenderAssetStatus.Unsupported, "Required native material cannot be decoded: " + resourceId + ". " + ex.Message); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException)
        { throw Invalid("Invalid render material '" + resourceId + "': " + ex.Message); }
    }

    static float Unit(JsonElement value)
    {
        float number = value.GetSingle();
        if (!float.IsFinite(number) || number < 0 || number > 1) throw Invalid("Material numeric components must be finite values from zero through one.");
        return number;
    }
    static string? Id(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        string? id = element.GetString();
        if (string.IsNullOrWhiteSpace(id)) throw Invalid("Texture IDs must be nonempty strings or null.");
        return id;
    }
    static MapRenderAssetRefusal Invalid(string detail) => new(MapRenderAssetStatus.Invalid, detail);
}
