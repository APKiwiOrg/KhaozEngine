using System;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;

namespace KhaozEngine.Tests.MapDoc.Foliage;

/// <summary>The closed payload 1 cases: every malformed, future and ceiling variant of the decorated document with the
/// structured rule and field its runtime refusal names, and the valid control with neither. Shared by the runtime theory and the schema
/// parity proof, so both evaluate the same cases.</summary>
internal static partial class FoliageFixtures
{
    internal static readonly (string Variant, MapDocumentRefusal? Reason, string? FieldPath)[] PayloadCases =
    [
        ("control", null, null),
        ("format-ten", MapDocumentRefusal.DocumentVersionUnsupported, "$.formatVersion"),
        ("root-null", MapDocumentRefusal.FoliageSectionNull, "$"),
        ("set-unknown", MapDocumentRefusal.SchemaMemberUnknown, "$.extra"),
        ("set-case", MapDocumentRefusal.SchemaMemberUnknown, "$.PayloadVersion"),
        ("set-duplicate", MapDocumentRefusal.SchemaMemberDuplicate, "$.foliage"),
        ("set-null", MapDocumentRefusal.SchemaValueRequired, "$.payloadVersion"),
        ("set-payload-two", MapDocumentRefusal.SchemaPayloadVersionUnsupported, "$.payloadVersion"),
        ("layer-payload-two", MapDocumentRefusal.SchemaPayloadVersionUnsupported, "$.layers[0].payloadVersion"),
        ("layer-unknown", MapDocumentRefusal.SchemaMemberUnknown, "$.layers[0].bogus"),
        ("layer-case", MapDocumentRefusal.SchemaMemberUnknown, "$.layers[0].Seed"),
        ("layer-case-duplicate", MapDocumentRefusal.SchemaMemberDuplicate, "$.foliage.layers[0]"),
        ("layer-missing", MapDocumentRefusal.SchemaValueRequired, "$.layers[0].seed"),
        ("layer-null", MapDocumentRefusal.SchemaValueRequired, "$.layers[0].seed"),
        ("id-empty", MapDocumentRefusal.SchemaValueRequired, "Layers[0].Id"),
        ("id-duplicate", MapDocumentRefusal.IdentityDuplicate, "Layers[1].Id"),
        ("cell-zero", MapDocumentRefusal.ValueOutsideRange, "CellSize"),
        ("cell-negative", MapDocumentRefusal.ValueOutsideRange, "CellSize"),
        ("cell-nonfinite", MapDocumentRefusal.ValueNonfinite, "$.layers[0].cellSize"),
        ("width-zero", MapDocumentRefusal.ValueOutsideRange, "Width"),
        ("depth-negative", MapDocumentRefusal.ValueOutsideRange, "Depth"),
        ("width-ceiling", MapDocumentRefusal.ValueOutsideRange, "Width"),
        ("depth-ceiling", MapDocumentRefusal.ValueOutsideRange, "Depth"),
        ("width-fraction", MapDocumentRefusal.ValueOutsideRange, "$.layers[0].width"),
        ("spacing-zero", MapDocumentRefusal.ValueOutsideRange, "Spacing"),
        ("scale-zero", MapDocumentRefusal.ValueOutsideRange, "ScaleMin"),
        ("scale-reversed", MapDocumentRefusal.BoundsInvalid, "ScaleMax"),
        ("models-empty", MapDocumentRefusal.ValueOutsideRange, "Models"),
        ("model-duplicate", MapDocumentRefusal.IdentityDuplicate, "Models[1].AssetId"),
        ("weight-zero", MapDocumentRefusal.ValueOutsideRange, "Models[0].Weight"),
        ("weight-negative", MapDocumentRefusal.ValueOutsideRange, "Models[1].Weight"),
        ("weight-sum", MapDocumentRefusal.WorkCapacityExceeded, "Models.Weight"),
        ("material-unknown", MapDocumentRefusal.ReferenceMissing, "AllowedVisibleMaterials[1]"),
        ("allowed-unsorted", MapDocumentRefusal.OrderInvalid, "AllowedVisibleMaterials[1]"),
        ("allowed-duplicate", MapDocumentRefusal.OrderInvalid, "AllowedVisibleMaterials[1]"),
        ("allowed-zero", MapDocumentRefusal.ReferenceMissing, "AllowedVisibleMaterials[0]"),
        ("allowed-fraction", MapDocumentRefusal.ValueOutsideRange, "$.layers[0].allowedVisibleMaterials[0]"),
        ("material-id-negative", MapDocumentRefusal.ValueOutsideRange, "$.layers[0].materials[2].id"),
        ("material-id-overflow", MapDocumentRefusal.ValueOutsideRange, "$.layers[0].materials[2].id"),
        ("material-id-fraction", MapDocumentRefusal.ValueOutsideRange, "$.layers[0].materials[2].id"),
        ("material-zero", MapDocumentRefusal.ValueOutsideRange, "Materials[2].Id"),
        ("material-duplicate", MapDocumentRefusal.IdentityDuplicate, "Materials[2].Id"),
        ("material-kind-future", MapDocumentRefusal.SchemaEnumUnsupported, "$.layers[0].materials[2].kind"),
        ("density-1023", MapDocumentRefusal.SchemaArrayLengthMismatch, "$.layers[0].chunks[0].density"),
        ("density-1025", MapDocumentRefusal.SchemaArrayLengthMismatch, "$.layers[0].chunks[0].density"),
        ("density-base64", MapDocumentRefusal.SchemaBase64Malformed, "$.layers[0].chunks[0].density"),
        ("density-noncanonical", MapDocumentRefusal.SchemaBase64Malformed, "$.layers[0].chunks[0].density"),
        ("density-token-ceiling", MapDocumentRefusal.WorkCapacityExceeded, "$.layers[0].chunks[0].density"),
        ("density-numeric-negative", MapDocumentRefusal.SchemaValueRequired, "$.layers[0].chunks[0].density"),
        ("density-numeric-256", MapDocumentRefusal.SchemaValueRequired, "$.layers[0].chunks[0].density"),
        ("density-numeric-fraction", MapDocumentRefusal.SchemaValueRequired, "$.layers[0].chunks[0].density"),
        ("chunk-duplicate", MapDocumentRefusal.IdentityDuplicate, "Chunks[1].Key"),
        ("chunk-outside-x", MapDocumentRefusal.FoliageChunkOutOfDomain, "ChunkDigests[1].Key.X"),
        ("chunk-outside-z", MapDocumentRefusal.FoliageChunkOutOfDomain, "ChunkDigests[1].Key.Z"),
        ("chunk-negative", MapDocumentRefusal.FoliageChunkOutOfDomain, "ChunkDigests[0].Key.X"),
        ("chunk-padding-x", MapDocumentRefusal.FoliagePaddingNonzero, "Chunk.Density"),
        ("chunk-padding-z", MapDocumentRefusal.FoliagePaddingNonzero, "Chunk.Density"),
        ("chunk-fraction", MapDocumentRefusal.ValueOutsideRange, "$.layers[0].chunks[0].x"),
        ("row-negative", MapDocumentRefusal.SchemaEnumUnsupported, "RowDirection"),
        ("row-unknown", MapDocumentRefusal.SchemaEnumUnsupported, "$.layers[0].rowDirection"),
        ("policy-version", MapDocumentRefusal.SchemaPayloadVersionUnsupported, "Policy.Version"),
        ("policy-id", MapDocumentRefusal.FoliagePolicyUnsupported, "Policy.Id"),
        ("support-surface-foreign", MapDocumentRefusal.SchemaMemberUnknown, "$.layers[0].support.referenceY"),
        ("support-space-missing", MapDocumentRefusal.SchemaValueRequired, "$.layers[0].support.searchAbove"),
        ("support-space-negative", MapDocumentRefusal.ValueOutsideRange, "OwnerSupports[0].Support.searchBelow"),
        ("support-null-member", MapDocumentRefusal.SchemaValueRequired, "$.layers[0].support.surfaceId"),
        ("support-kind-future", MapDocumentRefusal.SchemaEnumUnsupported, "$.layers[0].support.kind"),
        ("rule-kind-future", MapDocumentRefusal.SchemaEnumUnsupported, "$.layers[0].clearanceRules[0].kind"),
        ("rule-rect-reversed", MapDocumentRefusal.BoundsInvalid, "ClearanceRules[0].Footprint.MaxX"),
        ("rule-rect-empty", MapDocumentRefusal.BoundsInvalid, "ClearanceRules[0].Footprint.MaxZ"),
        ("rule-ordering", MapDocumentRefusal.SchemaMemberUnknown, "$.layers[0].clearanceRules[0].ordinal"),
        ("rule-tag-null", MapDocumentRefusal.SchemaValueRequired, "$.layers[0].clearanceRules[0].placementTag"),
        ("rule-id-empty", MapDocumentRefusal.SchemaValueRequired, "ClearanceRules[0].Id"),
        ("rule-id-duplicate", MapDocumentRefusal.IdentityDuplicate, "ClearanceRules[1].Id"),
        ("mask-id-empty", MapDocumentRefusal.SchemaValueRequired, "ExceptionMasks[0].Id"),
        ("mask-id-duplicate", MapDocumentRefusal.IdentityDuplicate, "ExceptionMasks[1].Id"),
        ("mask-rect-reversed", MapDocumentRefusal.BoundsInvalid, "ExceptionMasks[0].Footprint.MaxZ"),
        ("mask-surface-blank", MapDocumentRefusal.SchemaValueRequired, "ExceptionMasks[0].Support.surfaceId"),
        ("owner-surface-blank", MapDocumentRefusal.SchemaValueRequired, "OwnerSupports[0].Support.surfaceId"),
        ("owner-space-blank", MapDocumentRefusal.SchemaValueRequired, "OwnerSupports[0].Support.spaceId"),
        ("mask-witness", MapDocumentRefusal.SchemaMemberUnknown, "$.layers[0].exceptionMasks[0].witness"),
        ("mask-local-owner", MapDocumentRefusal.AuthoredOwnershipMismatch, "ExceptionMasks[0].Owner"),
        ("mask-wrong-kind", MapDocumentRefusal.AuthoredReferenceKindInvalid, "ExceptionMasks[0].Owner"),
        ("mask-kind-future", MapDocumentRefusal.SchemaEnumUnsupported, "$.layers[0].exceptionMasks[0].kind"),
        ("owner-wrong-kind", MapDocumentRefusal.AuthoredReferenceKindInvalid, "OwnerSupports[0].Owner"),
        ("owner-duplicate", MapDocumentRefusal.IdentityDuplicate, "OwnerSupports[1].Owner"),
        ("owner-space-missing", MapDocumentRefusal.SchemaValueRequired, "$.layers[0].ownerSupports[0].support.referenceY"),
        ("ordinal-duplicate", MapDocumentRefusal.IdentityDuplicate, "CompatibilityOrdinals[1].SurfaceId"),
        ("ordinal-missing", MapDocumentRefusal.SchemaValueRequired, "CompatibilityOrdinals[0].SurfaceId"),
        ("ordinal-negative", MapDocumentRefusal.ValueOutsideRange, "CompatibilityOrdinals[0].Ordinal"),
        ("ceiling-layers", MapDocumentRefusal.WorkCapacityExceeded, "$.layers"),
        ("ceiling-models", MapDocumentRefusal.WorkCapacityExceeded, "$.layers[0].models"),
        ("ceiling-allowed", MapDocumentRefusal.WorkCapacityExceeded, "$.layers[0].allowedVisibleMaterials"),
        ("ceiling-materials", MapDocumentRefusal.WorkCapacityExceeded, "$.layers[0].materials"),
        ("ceiling-rules", MapDocumentRefusal.WorkCapacityExceeded, "$.layers[0].clearanceRules"),
        ("ceiling-masks", MapDocumentRefusal.WorkCapacityExceeded, "$.layers[0].exceptionMasks"),
        ("ceiling-owners", MapDocumentRefusal.WorkCapacityExceeded, "$.layers[0].ownerSupports"),
        ("ceiling-chunks", MapDocumentRefusal.WorkCapacityExceeded, "$.layers[0].chunks"),
        ("ceiling-density", MapDocumentRefusal.WorkCapacityExceeded, "$.layers[4].chunks"),
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
