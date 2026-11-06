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

    [Fact]
    public void WriterBudgets_KeepTheFailedCountOrByteDomain()
    {
        var set = new MapSurfaceSet();
        set.Patches.Add(Patch(0).Key, Patch(0));
        var count = Assert.Throws<MapDocumentException>(() => MapSurfaceEmbedding.Check(set, 0, long.MaxValue));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, count.Reason);
        Assert.Equal("MaxEmbeddedPatches", count.Budget);
        Assert.Equal(1, count.ActualCount);
        Assert.Equal(0, count.Limit);
        MapSurfaceEmbedding.Check(set, 1, long.MaxValue);
        var empty = new MapSurfaceSet();
        var bytes = Assert.Throws<MapDocumentException>(() => MapSurfaceEmbedding.Check(empty, 0, 1));
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, bytes.Reason);
        Assert.Equal("maxEncodedBytes", bytes.FieldPath);
        Assert.Equal(1, bytes.ActualInteger);
        Assert.Equal(2, bytes.MinimumInteger);
        MapSurfaceEmbedding.Check(empty, 0, 2);
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
