using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceEmbeddingRefusalTests
{
    [Fact]
    public void NegativeByteBudget_IdentifiesTheOptionBeforePayloadWork()
    {
        var payload = new JsonObject();
        var error = Assert.Throws<MapDocumentException>(() =>
            MapSurfaceEmbeddedPayload.Encode(payload, -1, length => new byte[length]));
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, error.Reason);
        Assert.Equal("maxBytes", error.FieldPath);
        Assert.Equal(-1, error.ActualInteger);
        Assert.Equal(0, error.MinimumInteger);
        // Zero admits the option. The two-byte payload then reaches its distinct capacity rule.
        var zero = Assert.Throws<MapDocumentException>(() =>
            MapSurfaceEmbeddedPayload.Encode(payload, 0, length => new byte[length]));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, zero.Reason);
        Assert.Equal(0, zero.Limit);
        Assert.Equal(2, MapSurfaceEmbeddedPayload.Encode(payload, 2, length => new byte[length]).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllocatorSize_IdentifiesTheExpectedAndAvailableByteLengths(bool missing)
    {
        var payload = new JsonObject();
        var error = Assert.Throws<MapDocumentException>(() =>
            MapSurfaceEmbeddedPayload.Encode(payload, 2, length => missing ? null! : new byte[length + 1]));
        Assert.Equal(MapDocumentRefusal.SchemaArrayLengthMismatch, error.Reason);
        Assert.Equal("embedding allocation", error.SubjectKind);
        Assert.Equal(2, error.ExpectedByteCount);
        Assert.Equal(missing ? (long?)null : 3, error.ActualByteCount);
        Assert.Equal("{}", System.Text.Encoding.UTF8.GetString(MapSurfaceEmbeddedPayload.Encode(payload, 2, length => new byte[length])));
    }

    [Fact]
    public void ChangedPayload_IdentifiesTheMeasuredAndWrittenByteLengths()
    {
        var payload = new JsonObject { ["x"] = 11 };
        var error = Assert.Throws<MapDocumentException>(() => MapSurfaceEmbeddedPayload.Encode(payload, 8, length =>
        {
            payload["x"] = 1;
            return new byte[length];
        }));
        Assert.Equal(MapDocumentRefusal.StorageEmbeddingChanged, error.Reason);
        Assert.Equal(8, error.ExpectedByteCount);
        Assert.Equal(7, error.ActualByteCount);
        payload["x"] = 11;
        Assert.Equal("{\"x\":11}", System.Text.Encoding.UTF8.GetString(MapSurfaceEmbeddedPayload.Encode(payload, 8, length => new byte[length])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestingDepth_ReportsTheInclusiveContainerBudget(bool useNode)
    {
        JsonObject payload = Nested(17);
        using var json = JsonDocument.Parse(payload.ToJsonString());
        var error = Assert.Throws<MapDocumentException>(() =>
        {
            if (useNode) MapSurfaceEmbeddedPayload.Encode(payload, 1024, length => new byte[length]);
            else MapSurfaceEmbeddedPayload.Encode(json.RootElement, 1024, length => new byte[length]);
        });
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("MaxEmbeddingDepth", error.Budget);
        Assert.Equal(17, error.ActualCount);
        Assert.Equal(16, error.Limit);
        payload = Nested(16);
        using var control = JsonDocument.Parse(payload.ToJsonString());
        byte[] accepted = useNode ? MapSurfaceEmbeddedPayload.Encode(payload, 1024, length => new byte[length])
            : MapSurfaceEmbeddedPayload.Encode(control.RootElement, 1024, length => new byte[length]);
        Assert.Equal(payload.ToJsonString(new() { WriteIndented = false }), System.Text.Encoding.UTF8.GetString(accepted));
    }

    [Theory]
    [InlineData("single", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("double", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("half", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("scalar", MapDocumentRefusal.SchemaMalformed)]
    [InlineData("undefined", MapDocumentRefusal.SchemaMalformed)]
    public void ScalarAdmission_IdentifiesNonfiniteOrMalformedValues(string fault, MapDocumentRefusal expected)
    {
        object value = fault switch { "single" => float.NaN, "double" => double.PositiveInfinity,
            "half" => Half.NegativeInfinity, _ => DateTime.UnixEpoch };
        var payload = new JsonObject { ["x"] = JsonValue.Create(value) };
        var error = Assert.Throws<MapDocumentException>(() =>
        {
            if (fault == "undefined") MapSurfaceEmbeddedPayload.Encode(default(JsonElement), 128, length => new byte[length]);
            else MapSurfaceEmbeddedPayload.Encode(payload, 128, length => new byte[length]);
        });
        Assert.Equal(expected, error.Reason);
        if (fault != "undefined") Assert.Equal(value.GetType(), error.RecordType);
        payload["x"] = 1;
        Assert.Equal("{\"x\":1}", System.Text.Encoding.UTF8.GetString(MapSurfaceEmbeddedPayload.Encode(payload, 128, length => new byte[length])));
    }

    static JsonObject Nested(int containers)
    {
        var root = new JsonObject();
        var next = root;
        for (int i = 1; i < containers; i++) { var child = new JsonObject(); next["x"] = child; next = child; }
        return root;
    }
}
