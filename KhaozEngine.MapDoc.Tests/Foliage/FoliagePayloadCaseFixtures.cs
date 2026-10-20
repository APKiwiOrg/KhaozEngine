using System;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;

namespace KhaozEngine.Tests.MapDoc.Foliage;

/// <summary>The closed payload 1 cases: every malformed, future and ceiling variant of the decorated document with the
/// fragment its runtime refusal names, and the valid control with none. Shared by the runtime theory and the schema
/// parity proof, so both evaluate the same cases.</summary>
internal static partial class FoliageFixtures
{
    internal static readonly (string Variant, string? Fragment)[] PayloadCases =
    [
        ("control", null),
        ("format-ten", "newer than this engine supports"),
        ("root-null", "omit the section"),
        ("set-unknown", "unknown property 'extra'"),
        ("set-case", "unknown property 'PayloadVersion'"),
        ("set-duplicate", "duplicate property"),
        ("set-null", "must not be null"),
        ("set-payload-two", "only payload 1"),
        ("layer-payload-two", "only payload 1"),
        ("layer-unknown", "unknown property 'bogus'"),
        ("layer-case", "unknown property 'Seed'"),
        ("layer-case-duplicate", "duplicate property"),
        ("layer-missing", "missing required property 'seed'"),
        ("layer-null", "must not be null"),
        ("id-empty", "non-empty id"),
        ("id-duplicate", "duplicate layer id"),
        ("cell-zero", "cellSize must be finite and positive"),
        ("cell-negative", "cellSize must be finite and positive"),
        ("cell-nonfinite", "'cellSize' must be a finite number"),
        ("width-zero", "width and depth"),
        ("depth-negative", "width and depth"),
        ("width-ceiling", "width and depth"),
        ("depth-ceiling", "width and depth"),
        ("width-fraction", "'width' must be a 32-bit integer"),
        ("spacing-zero", "spacing must be finite and positive"),
        ("scale-zero", "scale range"),
        ("scale-reversed", "scale range"),
        ("models-empty", "models must hold"),
        ("model-duplicate", "listed twice"),
        ("weight-zero", "positive weight"),
        ("weight-negative", "positive weight"),
        ("weight-sum", "exceed the supported total"),
        ("material-unknown", "not a declared material"),
        ("allowed-unsorted", "sorted ascending"),
        ("allowed-duplicate", "sorted ascending"),
        ("allowed-zero", "not a declared material"),
        ("allowed-fraction", "material IDs are integers"),
        ("material-id-negative", "material IDs are integers"),
        ("material-id-overflow", "material IDs are integers"),
        ("material-id-fraction", "material IDs are integers"),
        ("material-zero", "void"),
        ("material-duplicate", "declared twice"),
        ("material-kind-future", "'kind' must be one of"),
        ("density-1023", "decodes to 1023 bytes"),
        ("density-1025", "decodes to 1025 bytes"),
        ("density-base64", "not valid base64"),
        ("density-noncanonical", "base64"),
        ("density-token-ceiling", "exceeds 1368 characters"),
        ("density-numeric-negative", "density must be a base64 string"),
        ("density-numeric-256", "density must be a base64 string"),
        ("density-numeric-fraction", "density must be a base64 string"),
        ("chunk-duplicate", "listed twice"),
        ("chunk-outside-x", "outside the density domain"),
        ("chunk-outside-z", "outside the density domain"),
        ("chunk-negative", "outside the density domain"),
        ("chunk-padding-x", "padding outside the density domain"),
        ("chunk-padding-z", "padding outside the density domain"),
        ("chunk-fraction", "'x' must be a 32-bit integer"),
        ("row-negative", "positive world Z"),
        ("row-unknown", "'rowDirection' must be one of"),
        ("policy-version", "only policy legacy-tile version 1"),
        ("policy-id", "only policy legacy-tile version 1"),
        ("support-surface-foreign", "unknown property 'referenceY'"),
        ("support-space-missing", "missing required property 'searchAbove'"),
        ("support-space-negative", "finite nonnegative"),
        ("support-null-member", "must not be null"),
        ("support-kind-future", "must be Surface or Space"),
        ("rule-kind-future", "'kind' must be one of"),
        ("rule-rect-reversed", "footprint must be finite"),
        ("rule-rect-empty", "footprint must be finite"),
        ("rule-ordering", "unknown property 'ordinal'"),
        ("rule-tag-null", "must not be null"),
        ("rule-id-empty", "every clearance rule needs a non-empty id"),
        ("rule-id-duplicate", "rule 'door-rule' is listed twice"),
        ("mask-id-empty", "every exception mask needs a non-empty id"),
        ("mask-id-duplicate", "mask 'roof-mask' is listed twice"),
        ("mask-rect-reversed", "mask 'roof-mask': footprint must be finite"),
        ("mask-surface-blank", "mask 'roof-mask' support: a Surface binding names only a surfaceId"),
        ("owner-surface-blank", "owner support 'house-1' support: a Surface binding names only a surfaceId"),
        ("owner-space-blank", "owner support 'house-1' support: a Space binding names a spaceId and no surfaceId"),
        ("mask-witness", "unknown property 'witness'"),
        ("mask-local-owner", "owner must be a World"),
        ("mask-wrong-kind", "owner must be a World"),
        ("mask-kind-future", "'kind' must be one of"),
        ("owner-wrong-kind", "owner must be a World"),
        ("owner-duplicate", "listed twice"),
        ("owner-space-missing", "missing required property 'referenceY'"),
        ("ordinal-duplicate", "listed twice"),
        ("ordinal-missing", "needs a surfaceId"),
        ("ordinal-negative", "must be nonnegative"),
        ("ceiling-layers", "'layers' exceeds 256"),
        ("ceiling-models", "'models' exceeds 256"),
        ("ceiling-allowed", "'allowedVisibleMaterials' exceeds 65535"),
        ("ceiling-materials", "'materials' exceeds 65535"),
        ("ceiling-rules", "'clearanceRules' exceeds 4096"),
        ("ceiling-masks", "'exceptionMasks' exceeds 4096"),
        ("ceiling-owners", "'ownerSupports' exceeds 65536"),
        ("ceiling-chunks", "'chunks' exceeds 16384"),
        ("ceiling-density", "exceeds 67108864 decoded bytes"),
    ];

    // The decorated document's saved text with one named defect.
    internal static string Malformed(string variant)
    {
        var root = JsonNode.Parse(MapDocumentFile.SaveText(Create("decorated").Document))!.AsObject();
        JsonObject foliage = root["foliage"]!.AsObject();
        JsonObject layer = root["foliage"]!["layers"]![0]!.AsObject();
        JsonObject rule = layer["clearanceRules"]![0]!.AsObject();
        JsonObject mask = layer["exceptionMasks"]![0]!.AsObject();
        JsonObject owner = layer["ownerSupports"]![0]!.AsObject();
        JsonArray chunks = layer["chunks"]!.AsArray();
        switch (variant)
        {
            case "control": break;
            case "format-ten": root["formatVersion"] = 10; break;
            case "root-null": root["foliage"] = null; break;
            case "set-unknown": foliage["extra"] = 1; break;
            case "set-case": Rename(foliage, "payloadVersion", "PayloadVersion"); break;
            case "set-duplicate":
                return root.ToJsonString().Replace("\"foliage\":{\"payloadVersion\":1,", "\"foliage\":{\"payloadVersion\":1,\"payloadVersion\":1,",
                    StringComparison.Ordinal);
            case "set-null": foliage["payloadVersion"] = null; break;
            case "set-payload-two": foliage["payloadVersion"] = 2; break;
            case "layer-payload-two": layer["payloadVersion"] = 2; break;
            case "layer-unknown": layer["bogus"] = 1; break;
            case "layer-case": Rename(layer, "seed", "Seed"); break;
            case "layer-case-duplicate": layer["Seed"] = 17; break;
            case "layer-missing": layer.Remove("seed"); break;
            case "layer-null": layer["seed"] = null; break;
            case "id-empty": layer["id"] = ""; break;
            case "id-duplicate": foliage["layers"]!.AsArray().Add(layer.DeepClone()); break;
            case "cell-zero": layer["cellSize"] = 0; break;
            case "cell-negative": layer["cellSize"] = -2; break;
            case "cell-nonfinite": layer["cellSize"] = 1e39; break;
            case "width-zero": layer["width"] = 0; break;
            case "depth-negative": layer["depth"] = -1; break;
            case "width-ceiling": layer["width"] = 1_048_577; break;
            case "depth-ceiling": layer["depth"] = 1_048_577; break;
            case "width-fraction": layer["width"] = 35.5; break;
            case "spacing-zero": layer["spacing"] = 0; break;
            case "scale-zero": layer["scaleMin"] = 0; break;
            case "scale-reversed": layer["scaleMax"] = 0.7; break;
            case "models-empty": layer["models"] = new JsonArray(); break;
            case "model-duplicate": layer["models"]![1]!["assetId"] = "reed-a"; break;
            case "weight-zero": layer["models"]![0]!["weight"] = 0; break;
            case "weight-negative": layer["models"]![1]!["weight"] = -1; break;
            case "weight-sum": layer["models"]![0]!["weight"] = 3e38; layer["models"]![1]!["weight"] = 3e38; break;
            case "material-unknown": layer["allowedVisibleMaterials"] = new JsonArray(10, 13); break;
            case "allowed-unsorted": layer["allowedVisibleMaterials"] = new JsonArray(11, 10); break;
            case "allowed-duplicate": layer["allowedVisibleMaterials"] = new JsonArray(10, 10); break;
            case "allowed-zero": layer["allowedVisibleMaterials"] = new JsonArray(0, 10); break;
            case "allowed-fraction": layer["allowedVisibleMaterials"] = new JsonArray(10.5); break;
            case "material-id-negative": layer["materials"]![2]!["id"] = -1; break;
            case "material-id-overflow": layer["materials"]![2]!["id"] = 65536; break;
            case "material-id-fraction": layer["materials"]![2]!["id"] = 12.5; break;
            case "material-zero": layer["materials"]![2]!["id"] = 0; break;
            case "material-duplicate": layer["materials"]![2]!["id"] = 10; break;
            case "material-kind-future": layer["materials"]![2]!["kind"] = "Lava"; break;
            case "density-1023": chunks[0]!["density"] = Convert.ToBase64String(new byte[1023]); break;
            case "density-1025": chunks[0]!["density"] = Convert.ToBase64String(new byte[1025]); break;
            case "density-base64": chunks[0]!["density"] = new string('!', 1368); break;
            case "density-noncanonical": chunks[0]!["density"] = Convert.ToBase64String(new byte[1024])[..^3] + "B=="; break;
            case "density-token-ceiling": chunks[0]!["density"] = Convert.ToBase64String(new byte[1024]) + "AAAA"; break;
            case "density-numeric-negative": chunks[0]!["density"] = new JsonArray(-1); break;
            case "density-numeric-256": chunks[0]!["density"] = new JsonArray(256); break;
            case "density-numeric-fraction": chunks[0]!["density"] = new JsonArray(0.5); break;
            case "chunk-duplicate": chunks[1]!["x"] = 0; break;
            case "chunk-outside-x": chunks[1]!["x"] = 2; break;
            case "chunk-outside-z": chunks[1]!["z"] = 1; break;
            case "chunk-negative": chunks[0]!["x"] = -1; break;
            case "chunk-padding-x": chunks[1]!["density"] = Padded(3, 0); break;
            case "chunk-padding-z": chunks[1]!["density"] = Padded(0, 3); break;
            case "chunk-fraction": chunks[0]!["x"] = 0.5; break;
            case "row-negative": layer["rowDirection"] = "NegativeZ"; break;
            case "row-unknown": layer["rowDirection"] = "Down"; break;
            case "policy-version": layer["policy"]!["version"] = 2; break;
            case "policy-id": layer["policy"]!["id"] = "geometric"; break;
            case "support-surface-foreign": layer["support"]!["referenceY"] = 1; break;
            case "support-space-missing":
                layer["support"] = new JsonObject { ["kind"] = "Space", ["spaceId"] = "hall", ["referenceY"] = 1, ["searchBelow"] = 0.5 };
                break;
            case "support-space-negative": owner["support"]!["searchBelow"] = -1; break;
            case "support-null-member": layer["support"]!["surfaceId"] = null; break;
            case "support-kind-future": layer["support"]!["kind"] = "Volume"; break;
            case "rule-kind-future": rule["kind"] = "Hedge"; break;
            case "rule-rect-reversed": rule["footprint"]!["minX"] = 1; break;
            case "rule-rect-empty": rule["footprint"]!["minZ"] = 0.25; break;
            case "rule-ordering": rule["ordinal"] = 1; break;
            case "rule-tag-null": rule["placementTag"] = null; break;
            case "rule-id-empty": rule["id"] = ""; break;
            case "rule-id-duplicate": layer["clearanceRules"]!.AsArray().Add(rule.DeepClone()); break;
            case "mask-id-empty": mask["id"] = ""; break;
            case "mask-id-duplicate": layer["exceptionMasks"]!.AsArray().Add(mask.DeepClone()); break;
            case "mask-rect-reversed": mask["footprint"]!["maxZ"] = -2; break;
            case "mask-surface-blank": mask["support"]!["surfaceId"] = " "; break;
            case "owner-surface-blank": owner["support"] = new JsonObject { ["kind"] = "Surface", ["surfaceId"] = "\t" }; break;
            case "owner-space-blank": owner["support"]!["spaceId"] = "  "; break;
            case "mask-witness": mask["witness"] = "upper"; break;
            case "mask-local-owner": mask["owner"]!["scope"] = "Local"; break;
            case "mask-wrong-kind": mask["owner"]!["kind"] = "Light"; break;
            case "mask-kind-future": mask["kind"] = "Indoor"; break;
            case "owner-wrong-kind": owner["owner"]!["kind"] = "Storey"; break;
            case "owner-duplicate": layer["ownerSupports"]!.AsArray().Add(owner.DeepClone()); break;
            case "owner-space-missing": owner["support"]!.AsObject().Remove("referenceY"); break;
            case "ordinal-duplicate": layer["compatibilityOrdinals"]![1]!["surfaceId"] = FloorId; break;
            case "ordinal-missing": layer["compatibilityOrdinals"]![0]!["surfaceId"] = ""; break;
            case "ordinal-negative": layer["compatibilityOrdinals"]![0]!["ordinal"] = -1; break;
            case "ceiling-layers":
                foliage["layers"] = new JsonArray(Enumerable.Range(0, 257).Select(i => RenamedLayer(layer, "layer-" + i)).ToArray());
                break;
            case "ceiling-models":
                layer["models"] = new JsonArray(Enumerable.Range(0, 257)
                    .Select(i => (JsonNode)new JsonObject { ["assetId"] = "m" + i, ["weight"] = 1 }).ToArray());
                break;
            // Each count refuses before any entry is read, so empty entries suffice.
            case "ceiling-allowed": layer["allowedVisibleMaterials"] = Empties(65_536, () => 1); break;
            case "ceiling-materials": layer["materials"] = Empties(65_536); break;
            case "ceiling-rules": layer["clearanceRules"] = Empties(4097); break;
            case "ceiling-masks": layer["exceptionMasks"] = Empties(4097); break;
            case "ceiling-owners": layer["ownerSupports"] = Empties(65_537); break;
            case "ceiling-chunks": layer["chunks"] = Empties(16_385); break;
            case "ceiling-density":
                foliage["layers"] = new JsonArray(Enumerable.Range(0, 5).Select(i =>
                {
                    JsonObject copy = RenamedLayer(layer, "layer-" + i);
                    copy["chunks"] = Empties(i < 4 ? 16_384 : 1);
                    return (JsonNode)copy;
                }).ToArray());
                break;
            default: throw new ArgumentException("Unknown variant " + variant, nameof(variant));
        }
        return root.ToJsonString();
    }

    static JsonArray Empties(int count, Func<JsonNode>? item = null) =>
        new(Enumerable.Range(0, count).Select(_ => item?.Invoke() ?? new JsonObject()).ToArray());

    // Chunk (1, 0) of the basic raster with one nonzero byte at a local address outside the 35 by 3 domain.
    static string Padded(int localX, int localZ)
    {
        byte[] bytes = BasicChunk(1).CopyDensity();
        bytes[localZ * MapFoliageChunk.Size + localX] = 1;
        return Convert.ToBase64String(bytes);
    }

    static void Rename(JsonObject obj, string from, string to)
    {
        JsonNode? value = obj[from];
        obj.Remove(from);
        obj[to] = value;
    }

    static JsonObject RenamedLayer(JsonObject layer, string id)
    {
        var copy = (JsonObject)layer.DeepClone();
        copy["id"] = id;
        return copy;
    }
}
