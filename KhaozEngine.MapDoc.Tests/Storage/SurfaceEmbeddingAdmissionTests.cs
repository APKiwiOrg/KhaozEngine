using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceEmbeddingAdmissionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArrayShapeAndPatchCount_IdentifyIndependentRules(bool node)
    {
        var shape = Refuses(node, () => Read(node, "{}"));
        Assert.Equal(MapDocumentRefusal.SchemaArrayRequired, shape.Reason);
        Assert.Equal("$.surfacePatches", shape.FieldPath);
        Assert.Empty(Read(node, "[]"));

        var patches = Enumerable.Range(0, 257).Select(i => JsonNode.Parse(MapSurfacePatchCodec.Encode(Patch(i)))).ToArray();
        var array = new JsonArray(patches);
        var capacity = Refuses(node, () => Read(node, array.ToJsonString()));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, capacity.Reason);
        Assert.Equal("MaxEmbeddedPatches", capacity.Budget);
        Assert.Equal(257, capacity.ActualCount);
        Assert.Equal(256, capacity.Limit);
        array.RemoveAt(256);
        Assert.Equal(256, Read(node, array.ToJsonString()).Count);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void InvalidReaderBudgets_IdentifyTheFirstFailedOption(bool node, bool patchBudget)
    {
        var error = Refuses(node, () => Read(node, "[]", patchBudget ? -1 : 0, patchBudget ? 2 : 1));
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, error.Reason);
        Assert.Equal(patchBudget ? "maxPatchBytes" : "maxEncodedBytes", error.FieldPath);
        Assert.Equal(patchBudget ? -1 : 1, error.ActualInteger);
        Assert.Equal(patchBudget ? 0 : 2, error.MinimumInteger);
        Assert.Empty(Read(node, "[]", 0, 2));
    }

    [Fact]
    public void NodeItems_IdentifyTheMalformedObjectAndDuplicatePatch()
    {
        var shape = Assert.Throws<MapDocumentException>(() => Read(true, "[false]"));
        Assert.Equal(MapDocumentRefusal.SchemaObjectRequired, shape.Reason);
        Assert.Equal("$.surfacePatches[0]", shape.FieldPath);
        Assert.Equal(0, shape.Index);
        string payload = System.Text.Encoding.UTF8.GetString(MapSurfacePatchCodec.Encode(Patch(0)));
        var duplicate = Assert.Throws<MapDocumentException>(() => Read(true, $"[{payload},{payload}]"));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, duplicate.Reason);
        Assert.Equal(new MapPatchKey("r", 0, 0), duplicate.Patch);
        Assert.Equal(1, duplicate.Index);
        Assert.Single(Read(true, $"[{payload}]"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ByteRefusal_PreservesTheProducerThroughTheJsonWrapper(bool node)
    {
        string array = "[" + System.Text.Encoding.UTF8.GetString(MapSurfacePatchCodec.Encode(Patch(0))) + "]";
        var error = Refuses(node, () => Read(node, array, 0));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("MaxEmbeddingBytes", error.Budget);
        Assert.Equal(0, error.Limit);
        Assert.True(error.ActualByteCount > 0);
        Assert.Single(Read(node, array));
    }

    [Fact]
    public void JsonWrapper_KeepsAnUntypedConsumerCauseWithoutInventingAReason()
    {
        var original = new MapDocumentException("consumer refusal");
        string array = "[" + System.Text.Encoding.UTF8.GetString(MapSurfacePatchCodec.Encode(Patch(0))) + "]";
        var error = Assert.Throws<JsonException>(() => Read(false, array, allocate: _ => throw original));
        Assert.Same(original, error.InnerException);
        Assert.Equal(MapDocumentRefusal.SchemaMalformed, MapDocumentJsonErrors.Wrap(error, "map", "invalid").Reason);
        Assert.Single(Read(false, array));
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    public void WriterBudgets_RunTheRealSaveAtTheCountAndByteLimits(bool countBudget, int patchCount)
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(patchCount);
        string before = MapDocumentFile.SaveText(doc);
        // The canonical array has two brackets and one comma between adjacent payloads.
        long exactBytes = 2 + Math.Max(0, patchCount - 1) + doc.Surfaces.Patches.Values.Sum(p => (long)MapSurfacePatchCodec.Encode(p).Length);
        int countLimit = countBudget ? 1 : 2;
        long byteLimit = countBudget ? exactBytes : exactBytes - 1;
        var error = Assert.Throws<MapDocumentException>(() =>
            MapDocumentFile.SaveTextWithEmbeddingLimitsForTests(doc, countLimit, byteLimit));
        Assert.Equal(patchCount == 0 ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        if (patchCount == 0)
        {
            Assert.Equal("maxEncodedBytes", error.FieldPath);
            Assert.Equal(1, error.ActualInteger);
            Assert.Equal(2, error.MinimumInteger);
        }
        else
        {
            Assert.Equal(countBudget ? "MaxEmbeddedPatches" : "MaxEmbeddingBytes", error.Budget);
            Assert.Equal(countBudget ? countLimit : byteLimit, error.Limit);
            if (countBudget) Assert.Equal(2, error.ActualCount);
            else Assert.Equal(exactBytes, error.ActualByteCount);
        }
        Assert.Equal(before, MapDocumentFile.SaveText(doc));
        if (countBudget) doc.Surfaces.Patches.Remove(doc.Surfaces.Patches.Keys.Last());
        int acceptedCount = countBudget ? countLimit : patchCount;
        string accepted = MapDocumentFile.SaveTextWithEmbeddingLimitsForTests(doc, countBudget ? countLimit : 2, exactBytes);
        Assert.Equal(acceptedCount, MapDocumentFile.LoadText(accepted).Surfaces.Patches.Count);
        if (countBudget) doc.Surfaces.Patches.Remove(doc.Surfaces.Patches.Keys.Last());
        else exactBytes++;
        Assert.NotEmpty(MapDocumentFile.SaveTextWithEmbeddingLimitsForTests(doc, countBudget ? countLimit : 2, exactBytes));
    }

    static MapDocumentException Refuses(bool node, Action action)
    {
        if (node) return Assert.Throws<MapDocumentException>(action);
        var error = Assert.Throws<MapSurfaceEmbeddingJsonException>(action);
        Assert.IsAssignableFrom<JsonException>(error);
        var wrapped = MapDocumentJsonErrors.Wrap(error, "map", "invalid");
        Assert.Equal(error.Reason, wrapped.Reason);
        Assert.Equal(error.Budget, wrapped.Budget);
        Assert.Equal(error.FieldPath, wrapped.FieldPath);
        Assert.Equal(error.ActualCount, wrapped.ActualCount);
        Assert.Equal(error.ActualByteCount, wrapped.ActualByteCount);
        Assert.Equal(error.Limit, wrapped.Limit);
        return wrapped;
    }

    static IReadOnlyList<MapSurfacePatch> Read(bool node, string array,
        int patchCap = MapSurfacePatchCodec.MaxEncodedBytes, long byteCap = MapSurfaceEmbedding.MaxEncodedBytes,
        Func<int, byte[]>? allocate = null)
    {
        allocate ??= size => new byte[size];
        if (node)
        {
            var root = new JsonObject { ["surfacePatches"] = JsonNode.Parse(array) };
            var set = new MapSurfaceSet();
            MapSurfaceEmbedding.Read(root, set, patchCap, byteCap, allocate);
            return set.Patches.Values.ToArray();
        }
        var options = MapDocumentFile.CreateCompactOptions(MapDocRegistry.CreateDefault());
        options.Converters.Insert(0, new MapSurfaceEmbeddingConverter(patchCap, byteCap, allocate));
        return JsonSerializer.Deserialize<List<MapSurfacePatch>>(array, options)!;
    }

    static MapSurfacePatch Patch(int slot) => new()
    {
        Key = new("r", slot, 0), Width = 1, Depth = 1, Heights = new int[4],
        Cells = new MapSurfaceCell[1], Presence = [1],
    };
}
