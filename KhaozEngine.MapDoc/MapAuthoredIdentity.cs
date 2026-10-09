using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc.Assets;

namespace KhaozEngine.MapDoc;

/// <summary>Complete in-memory native identity. Never trusts persisted tile hashes or a loaded window.</summary>
public static class MapAuthoredIdentity
{
    public static string Compute(MapDocument document, MapAssetClosure assets, MapResolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(options);
        Validate(document, assets, options);
        // The whole writer normalizes empty sculpt storage and excludes the persisted tile index.
        JsonObject root = JsonNode.Parse(MapDocumentFile.SaveText(document))!.AsObject();
        root.Remove("$schema");
        root.Remove("displayName");
        foreach (JsonNode? placement in root["placements"]!.AsArray()) placement!.AsObject().Remove("displayName");
        foreach (string list in new[] { "placements", "spawns", "playerSpawns", "nativeAssets" })
        {
            JsonArray values = root[list]!.AsArray();
            var ordered = values.OrderBy(n => n!["id"]!.GetValue<string>(), StringComparer.Ordinal).ToArray();
            values.Clear();
            foreach (JsonNode? value in ordered) values.Add(value);
        }
        return MapCanonical.HashHex(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("domain", "kemap/native-authored/1");
            writer.WritePropertyName("document");
            WriteNormalized(writer, root);
            writer.WriteString("closure", assets.Hash);
            writer.WriteString("builderId", options.BuilderId);
            writer.WriteNumber("builderVersion", options.BuilderVersion);
            writer.WriteString("optionsHash", options.OptionsHash);
            writer.WriteNumber("resolverVersion", options.ResolverVersion);
            writer.WriteEndObject();
        });
    }

    static void Validate(MapDocument doc, MapAssetClosure assets, MapResolveOptions options)
    {
        if (doc.ResolverIdentity is { ResolverVersion: 2 })
            throw new MapDocumentException("Resolver version 2 requires MapAuthoredIdentityV2.");
        if (options.ResolverVersion != 1 || string.IsNullOrWhiteSpace(options.BuilderId) ||
            options.BuilderVersion <= 0 || string.IsNullOrWhiteSpace(options.OptionsHash))
            throw new MapDocumentException("Native build identity requires supported resolver, builder ID, positive version and options hash.");
        MapBoundDocumentValidation.Validate(doc, assets);
    }

    static void WriteNormalized(Utf8JsonWriter writer, JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            writer.WriteStartObject();
            foreach (var member in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(member.Key);
                WriteNormalized(writer, member.Value);
            }
            writer.WriteEndObject();
        }
        else if (node is JsonArray array)
        {
            writer.WriteStartArray();
            foreach (JsonNode? item in array) WriteNormalized(writer, item);
            writer.WriteEndArray();
        }
        else if (node is null) writer.WriteNullValue();
        else node.WriteTo(writer);
    }
}
