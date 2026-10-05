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
        if (doc.Tiles is { IsPartial: true }) throw new MapDocumentException("Complete native identity requires every tile to be loaded.");
        if (doc.ResolverIdentity is not { PayloadVersion: 1, ResolverVersion: 1 } || options.ResolverVersion != 1)
            throw new MapDocumentException("Native identity requires supported payload and resolver version 1.");
        if (doc.PlayableBounds is null) throw new MapDocumentException("Native identity requires playable bounds.");
        if (string.IsNullOrWhiteSpace(options.BuilderId) || options.BuilderVersion <= 0 || string.IsNullOrWhiteSpace(options.OptionsHash))
            throw new MapDocumentException("Native build identity requires builder ID, positive version and options hash.");
        var errors = MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault());
        if (errors.Count != 0) throw new MapDocumentException(string.Join("\n", errors));
        var roots = new HashSet<MapAssetRef>(doc.NativeAssets);
        if (roots.Count != doc.NativeAssets.Count || roots.Count != assets.Roots.Count || !roots.SetEquals(assets.Roots))
            throw new MapDocumentException("Native document roots do not match the verified asset closure.");
        if (!float.IsFinite(doc.Bounds.MinX) || !float.IsFinite(doc.Bounds.MinZ) ||
            !float.IsFinite(doc.Bounds.MaxX) || !float.IsFinite(doc.Bounds.MaxZ))
            throw new MapDocumentException("Native storage bounds must be finite.");
        foreach (MapPlacement p in doc.Placements)
        {
            if (string.IsNullOrWhiteSpace(p.AssetId)) throw new MapDocumentException($"Placement '{p.Id}' requires a native asset ID.");
            assets.GetAsset(p.AssetId);
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Z) || (p.Y is { } y && !float.IsFinite(y)) ||
                !float.IsFinite(p.Yaw) || !float.IsFinite(p.Scale) || p.Scale <= 0 || p.Tags is null || p.Tags.Any(t => t is null))
                throw new MapDocumentException($"Placement '{p.Id}' has invalid transform or tags.");
        }
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
