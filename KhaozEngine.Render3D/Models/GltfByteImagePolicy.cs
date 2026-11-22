using System;
using System.Text.Json;

namespace KhaozEngine.Render3D;

internal static class GltfByteImagePolicy
{
    // SharpGLTF resolves every image URI, even an unused one. Its stream reader has no resolver for
    // external files, so refuse them in the JSON hook before it tries to resolve the images.
    internal static string Validate(string json, string identity)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("images", out JsonElement images)
            || images.ValueKind != JsonValueKind.Array)
            return json;

        int index = 0;
        foreach (JsonElement image in images.EnumerateArray())
        {
            if (image.ValueKind == JsonValueKind.Object && image.TryGetProperty("uri", out JsonElement uri)
                && uri.ValueKind == JsonValueKind.String
                && !uri.GetString()!.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                throw new GltfImportDataException(GltfImportRefusal.ExternalImageInByteImport,
                    $"glTF asset '{identity}' image {index} names a resource outside the supplied GLB bytes.")
                { Identity = identity, ImageIndex = index };
            }
            index++;
        }
        return json;
    }
}
