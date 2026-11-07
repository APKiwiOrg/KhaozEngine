using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageAdmissionRefusalTests
{
    [Theory]
    [InlineData("missing", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("type", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("version", MapDocumentRefusal.SchemaPayloadVersionUnsupported)]
    public void Migration_ReportsTheVersionRuleWithoutChangingTheSource(string fault, MapDocumentRefusal reason)
    {
        var source = new JsonObject();
        if (fault == "type") source["formatVersion"] = "8";
        else if (fault == "version") source["formatVersion"] = 7;
        string before = source.ToJsonString();
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageMigration.Upgrade(source));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("formatVersion", error.FieldPath);
        if (fault == "version") { Assert.Equal(7, error.ActualVersion); Assert.Equal(new[] { 8, 9 }, error.SupportedVersions); }
        Assert.Equal(before, source.ToJsonString());
        source["formatVersion"] = 8;
        Assert.Equal(9, MapFoliageMigration.Upgrade(source)["formatVersion"]!.GetValue<int>());
        Assert.Equal(8, source["formatVersion"]!.GetValue<int>());
        source["formatVersion"] = 9;
        Assert.Equal(9, MapFoliageMigration.Upgrade(source)["formatVersion"]!.GetValue<int>());
    }

    [Fact]
    public void AdvertisedDensity_RefusesUnavailableAndDuplicateChunkKeys()
    {
        var layer = FoliageFixtures.BasicLayer();
        var chunk = layer.Chunks[0];
        var row = new MapFoliageChunkDigest(chunk.Key, chunk.Sha256);
        var missing = MapFoliageLayer.FromStorage(layer, [row], []);
        var error = Assert.Throws<MapDocumentException>(() => missing.DensityAt(0, 0));
        Assert.Equal(MapDocumentRefusal.FoliageChunkUnavailable, error.Reason);
        Assert.Equal(layer.Id, error.LayerId);
        Assert.Equal(chunk.Key, error.FoliageChunk);
        var duplicate = Assert.Throws<MapDocumentException>(() => MapFoliageLayer.FromStorage(layer, [row, row], []));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, duplicate.Reason);
        Assert.Equal(layer.Id, duplicate.LayerId);
        Assert.Equal(chunk.Key, duplicate.FoliageChunk);
        var resident = MapFoliageLayer.FromStorage(layer, [row], [chunk]);
        Assert.Equal(chunk.DensityAt(1, 0), resident.DensityAt(1, 0));
        Assert.Equal(0, missing.DensityAt(34, 0));
    }

    [Theory]
    [InlineData("kind", MapDocumentRefusal.SchemaEnumUnsupported, "kind")]
    [InlineData("surface-missing", MapDocumentRefusal.SchemaValueRequired, "surfaceId")]
    [InlineData("surface-foreign", MapDocumentRefusal.AuthoredOwnershipMismatch, "spaceId")]
    [InlineData("space-missing", MapDocumentRefusal.SchemaValueRequired, "spaceId")]
    [InlineData("space-foreign", MapDocumentRefusal.AuthoredOwnershipMismatch, "surfaceId")]
    [InlineData("height", MapDocumentRefusal.SchemaValueRequired, "referenceY")]
    [InlineData("below", MapDocumentRefusal.SchemaValueRequired, "searchBelow")]
    [InlineData("above", MapDocumentRefusal.SchemaValueRequired, "searchAbove")]
    public void BindingWriter_IdentifiesTheFirstMissingOrForeignMember(string fault, MapDocumentRefusal reason, string field)
    {
        var surface = FoliageFixtures.Surface(FoliageFixtures.FloorId);
        var space = new MapSupportBinding(MapSupportBindingKind.Space, null, "hall", 0, 0, 0);
        var binding = fault switch
        {
            "kind" => surface with { Kind = (MapSupportBindingKind)99 },
            "surface-missing" => surface with { SurfaceId = null },
            "surface-foreign" => surface with { SpaceId = "hall" },
            "space-missing" => space with { SpaceId = null },
            "space-foreign" => space with { SurfaceId = FoliageFixtures.FloorId },
            "height" => space with { ReferenceY = null },
            "below" => space with { SearchBelow = null },
            _ => space with { SearchAbove = null },
        };
        var layer = FoliageFixtures.ClearanceLayer(FoliageFixtures.BasicLayer(), support: binding);
        var error = Assert.Throws<MapDocumentException>(() => JsonSerializer.Serialize(new MapFoliageSet(1, [layer])));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("support." + field, error.FieldPath);
        Assert.Equal(layer.Id, error.LayerId);
        var control = FoliageFixtures.ClearanceLayer(layer, support: fault.StartsWith("surface", StringComparison.Ordinal) || fault == "kind" ? surface : space);
        Assert.Single(JsonSerializer.Deserialize<MapFoliageSet>(JsonSerializer.Serialize(new MapFoliageSet(1, [control])))!.Layers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DensityTokenLimits_ReportTheMeasuredBudgetWithAnExactControl(bool escaped)
    {
        JsonObject root = Section();
        root["layers"]![0]!["chunks"]![0]!["density"] = "TOKEN";
        string token = escaped ? string.Concat(Enumerable.Repeat("\\u0041", MapFoliagePayloadLimits.MaxTokenChars + 1))
            : new string('A', MapFoliagePayloadLimits.MaxTokenChars + 1);
        string text = root.ToJsonString().Replace("\"TOKEN\"", "\"" + token + "\"", StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(text);
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageJson.Read(json.RootElement));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("$.layers[0].chunks[0].density", error.FieldPath);
        Assert.Equal(FoliageFixtures.LayerId, error.LayerId);
        Assert.Equal(new MapFoliageChunkKey(0, 0), error.FoliageChunk);
        Assert.Equal(escaped ? "density token UTF-8 bytes" : "density token characters", error.Budget);
        if (escaped) Assert.Equal(token.Length + 2, error.ActualByteCount);
        else Assert.Equal(token.Length, error.ActualCount);
        Assert.Equal(escaped ? 6 * MapFoliagePayloadLimits.MaxTokenChars + 2 : MapFoliagePayloadLimits.MaxTokenChars, error.Limit);
        root["layers"]![0]!["chunks"]![0]!["density"] = Convert.ToBase64String(new byte[MapFoliageChunk.ByteLength]);
        string valid = root.ToJsonString();
        if (escaped)
        {
            string density = Convert.ToBase64String(new byte[MapFoliageChunk.ByteLength]);
            string raw = string.Concat(density.Select(c => "\\u" + ((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture)));
            valid = valid.Replace(density, raw, StringComparison.Ordinal);
        }
        using JsonDocument control = JsonDocument.Parse(valid);
        Assert.Single(MapFoliageJson.Read(control.RootElement).Layers);
    }

    [Fact]
    public void ArrayCapacity_ReportsTheFieldAndAcceptsTheExactLimit()
    {
        using JsonDocument json = JsonDocument.Parse("{\"models\":[{},{}]}");
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageJson.RequireArray(json.RootElement, "models", "layer", 1));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Null(error.FieldPath);
        Assert.Equal("models", error.Budget);
        Assert.Equal(2, error.ActualCount);
        Assert.Equal(1, error.Limit);
        var converter = MapFoliageJsonException.FromDocument(error);
        Assert.Equal(error.Budget, converter.Budget);
        Assert.Equal(error.ActualCount, converter.ActualCount);
        Assert.Equal(error.Limit, converter.Limit);
        var wrapped = converter.ToDocument("foliage.json", "refused", null, null, null);
        Assert.Equal(converter.Budget, wrapped.Budget);
        Assert.Equal(converter.ActualCount, wrapped.ActualCount);
        Assert.Equal(converter.Limit, wrapped.Limit);
        Assert.Equal(2, MapFoliageJson.RequireArray(json.RootElement, "models", "layer", 2).GetArrayLength());
    }

    [Fact]
    public void DocumentDensityCount_ChargesBeforeStoredLayersMaterialize()
    {
        JsonObject root = Section();
        root["packs"] = new JsonArray();
        JsonObject template = root["layers"]![0]!.AsObject();
        var layers = new JsonArray();
        int fullLayers = MapFoliagePayloadLimits.MaxDocumentChunks / MapFoliagePayloadLimits.MaxChunksPerLayer;
        for (int i = 0; i <= fullLayers; i++)
        {
            var layer = (JsonObject)template.DeepClone();
            layer["id"] = "layer-" + i;
            layer["chunks"] = new JsonArray(Enumerable.Range(0, i < fullLayers ? MapFoliagePayloadLimits.MaxChunksPerLayer : 1)
                .Select(_ => (JsonNode)new JsonObject()).ToArray());
            layers.Add(layer);
        }
        root["layers"] = layers;
        using JsonDocument json = JsonDocument.Parse(root.ToJsonString());
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageJson.ReadStoredMetadata(json.RootElement));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("decoded density bytes per document", error.Budget);
        Assert.Equal(MapFoliagePayloadLimits.MaxDensityBytes + MapFoliageChunk.ByteLength, error.ActualByteCount);
        Assert.Equal(MapFoliagePayloadLimits.MaxDensityBytes, error.Limit);
        // Stored metadata counts descriptors here, while the pack reader owns their density payloads.
        layers.RemoveAt(fullLayers);
        using JsonDocument control = JsonDocument.Parse(root.ToJsonString());
        Assert.Equal(fullLayers, MapFoliageJson.ReadStoredMetadata(control.RootElement).Layers.Count);
    }

    internal static JsonObject Section() => JsonSerializer.SerializeToNode(new MapFoliageSet(1, [FoliageFixtures.BasicLayer()]))!.AsObject();
}
