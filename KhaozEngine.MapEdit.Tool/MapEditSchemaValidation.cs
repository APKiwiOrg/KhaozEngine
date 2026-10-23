using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Content;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEdit;

/// <summary>Serializes the content currently resident in a windowed map one tile at a time and checks each
/// tile against the packaged tile schema. Kept separate from <see cref="MapEditSession"/> so the session only
/// coordinates validation scopes and result reporting. Coordinates come from a fresh spatial index rather
/// than the document's stored tile entries, because an edit can occupy a previously empty coordinate before
/// the next save refreshes that stored index.</summary>
internal static class MapEditSchemaValidation
{
    internal static ValidationReport ValidateComplete(MapDocument doc, MapDocRegistry registry)
    {
        string schema = MapDocumentSchema.GetJson();
        if (doc.Tiles is null)
            return JsonSchemaValidator.Validate(MapDocumentFile.SaveText(doc, registry), schema);

        // The tiled document has no embedded patch array. Validate its roots and each bounded payload separately.
        MapSurfaceSaveGuard.Whole(doc.Surfaces);
        var root = MapTiledFile.GlobalsOnly(doc);
        root.Placements = doc.Placements; root.Spawns = doc.Spawns; root.PlayerSpawns = doc.PlayerSpawns;
        root.TerrainOverrides = doc.TerrainOverrides;
        var options = MapDocumentFile.CreateOptions(registry, write: true);
        var errors = new List<string>(JsonSchemaValidator.Validate(JsonSerializer.Serialize(root, options), schema).Errors);
        var definitions = JsonNode.Parse(schema)!;
        string patchSchema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$ref"] = "#/$defs/surfacePatch",
            ["$defs"] = definitions["$defs"]!.DeepClone(),
        }.ToJsonString();
        foreach (var patch in doc.Surfaces.Patches.Values)
        {
            string json = System.Text.Encoding.UTF8.GetString(MapSurfacePatchCodec.Encode(patch));
            foreach (string error in JsonSchemaValidator.Validate(json, patchSchema).Errors)
                errors.Add($"patch '{patch.Key}': {error}");
        }
        return new ValidationReport(errors.Count == 0, errors);
    }

    internal static ValidationReport ValidateLoadedTiles(MapDocument doc, MapDocRegistry registry)
    {
        MapSpatialIndex spatial = MapSpatialIndex.Build(doc);
        JsonSerializerOptions options = MapDocumentFile.CreateOptions(registry, write: true);
        string schema = MapDocumentSchema.GetTileJson();
        var errors = new List<string>();

        foreach (MapTileCoord coord in spatial.OccupiedTiles)
        {
            string json;
            try
            {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    MapTileLists lists = MapTileLists.Of(spatial, coord);
                    MapCanonical.WriteTileBody(writer, lists, options, schemaRef: null);
                    writer.Flush();
                }
                json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
            {
                errors.Add($"tile ({coord.X}, {coord.Z}): could not serialize for schema validation. {ex.Message}");
                continue;
            }

            ValidationReport report = JsonSchemaValidator.Validate(json, schema);
            foreach (string error in report.Errors)
                errors.Add($"tile ({coord.X}, {coord.Z}): {error}");
        }

        return new ValidationReport(errors.Count == 0, errors);
    }
}
