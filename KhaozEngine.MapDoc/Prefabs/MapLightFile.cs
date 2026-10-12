using System;
using System.Text.Json;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>The closed payload-1 leaf light resource. Position and radius are metres in the owner's frame. The owner
/// is null or the typed Local placement token <c>$owner</c>, which enumeration binds to the consuming placement.</summary>
public static class MapLightFile
{
    internal const string Owner = "$owner";

    public static MapLocalLightDoc Read(ReadOnlyMemory<byte> bytes, string resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        if (bytes.Length > MapPrefabJson.Limits.MaxPayloadBytes) throw new MapDocumentException("Light payload byte limit exceeded.");
        try
        {
            using JsonDocument json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            MapLocalLightDoc light = MapPrefabJson.Read<MapLocalLightDoc>(json.RootElement);
            Require(light, resourceId);
            return light;
        }
        catch (JsonException ex) { throw new MapDocumentException("Invalid light payload '" + resourceId + "'.", ex); }
    }

    public static byte[] Write(MapLocalLightDoc light)
    {
        ArgumentNullException.ThrowIfNull(light);
        Require(light, light.Id);
        return MapPrefabJson.Bytes(light);
    }

    static void Require(MapLocalLightDoc light, string resourceId)
    {
        MapPrefabValidation.RequireLight(light);
        if (light.Owner is { } owner && owner != new MapPrefabReference(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, Owner))
            throw new MapDocumentException($"Light payload '{resourceId}' owner must be null or the Local placement token {Owner}.");
    }
}
