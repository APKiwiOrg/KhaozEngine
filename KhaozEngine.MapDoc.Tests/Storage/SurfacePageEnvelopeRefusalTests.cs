using System;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfacePageEnvelopeRefusalTests
{
    static readonly MapSlotRect Covers = new(0, 0, 1, 1);
    static readonly string Digest = new('a', 64);
    static readonly MapDirectoryPageRef DirectoryRef = new("r", Covers, Digest);

    [Theory]
    [InlineData("root", MapDocumentRefusal.SchemaObjectRequired, "$")]
    [InlineData("count", MapDocumentRefusal.SchemaArrayLengthMismatch, "$")]
    [InlineData("missing", MapDocumentRefusal.SchemaMemberMissing, "$.surfaceId")]
    [InlineData("duplicate", MapDocumentRefusal.SchemaMemberDuplicate, "$.covers.minX")]
    [InlineData("null", MapDocumentRefusal.SchemaMemberNull, "$.pages[0].sha256")]
    [InlineData("surface", MapDocumentRefusal.AuthoredValueMismatch, "$.surfaceId")]
    [InlineData("covers", MapDocumentRefusal.BoundsInvalid, "$.covers")]
    public void DirectoryEnvelope_IdentifiesTheFirstInvalidField(string fault, MapDocumentRefusal reason, string field)
    {
        byte[] valid = MapSurfacePages.EncodeDirectory("r", Covers, [new(Covers, Digest, 1)]);
        var root = JsonNode.Parse(valid)!.AsObject();
        switch (fault)
        {
            case "count": root.Remove("surfaceId"); break;
            case "missing": root.Remove("surfaceId"); root["extra"] = "r"; break;
            case "null": root["pages"]![0]!["sha256"] = null; break;
            case "surface": root["surfaceId"] = "other"; break;
            case "covers": root["covers"]!["maxXExclusive"] = 2; break;
        }
        string text = fault == "root" ? "[]" : root.ToJsonString();
        if (fault == "duplicate") text = text.Replace("\"covers\":{", "\"covers\":{\"minX\":99,", StringComparison.Ordinal);
        var error = Assert.Throws<MapDocumentException>(() => MapSurfacePages.DecodeDirectory(Encoding.UTF8.GetBytes(text), DirectoryRef));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        Assert.Single(MapSurfacePages.DecodeDirectory(valid, DirectoryRef));
    }

    [Theory]
    [InlineData("surface", MapDocumentRefusal.AuthoredValueMismatch, "$.surfaceId")]
    [InlineData("covers", MapDocumentRefusal.BoundsInvalid, "$.covers")]
    [InlineData("count", MapDocumentRefusal.SchemaArrayLengthMismatch, "$.entries")]
    public void IndexEnvelope_KeepsIdentityBoundsAndDeclaredCountDistinct(string fault, MapDocumentRefusal reason, string field)
    {
        var entry = new MapSurfaceIndexEntry(new("r", 0, 0), new(0, 0, 1, 1), 0, 0, Digest, Digest, [], [], [], [], false);
        byte[] bytes = MapSurfacePages.EncodeIndex("r", Covers, [entry]);
        var expected = new MapIndexPageRef(fault == "covers" ? new(0, 0, 2, 1) : Covers, Digest, fault == "count" ? 2 : 1);
        var error = Assert.Throws<MapDocumentException>(() => MapSurfacePages.DecodeIndex(bytes, fault == "surface" ? "other" : "r", expected));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        if (fault == "count") { Assert.Equal(1, error.ActualCount); Assert.Equal(2, error.ExpectedCount); }
        Assert.Single(MapSurfacePages.DecodeIndex(bytes, "r", new(Covers, Digest, 1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PageByteAdmission_ReportsTheEmptyDomainOrInclusiveCapacity(bool oversized)
    {
        byte[] bytes = new byte[oversized ? MapSurfaceStorageLayout.MaxPageBytes + 1 : 0];
        var error = Assert.Throws<MapDocumentException>(() => MapSurfacePages.DecodeDirectory(bytes, DirectoryRef));
        Assert.Equal(oversized ? MapDocumentRefusal.WorkCapacityExceeded : MapDocumentRefusal.ValueOutsideRange, error.Reason);
        Assert.Equal(bytes.LongLength, error.ActualByteCount);
        Assert.Equal("$", error.FieldPath);
        if (oversized) { Assert.Equal("MaxPageBytes", error.Budget); Assert.Equal(MapSurfaceStorageLayout.MaxPageBytes, error.Limit); }
        else Assert.Equal(1, error.MinimumInteger);
        byte[] valid = MapSurfacePages.EncodeDirectory("r", Covers, [new(Covers, Digest, 1)]);
        var capped = new byte[MapSurfaceStorageLayout.MaxPageBytes];
        capped.AsSpan().Fill((byte)' ');
        valid.CopyTo(capped, 0);
        Assert.Single(MapSurfacePages.DecodeDirectory(capped, DirectoryRef));
    }
}
