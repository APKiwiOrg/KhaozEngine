using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceDirectoryRefusalTests
{
    [Theory]
    [InlineData("array", MapDocumentRefusal.SchemaArrayRequired, "surfaceStorage")]
    [InlineData("item", MapDocumentRefusal.SchemaObjectRequired, "surfaceStorage[0]")]
    [InlineData("count", MapDocumentRefusal.SchemaArrayLengthMismatch, "surfaceStorage[0]")]
    [InlineData("surface", MapDocumentRefusal.SchemaValueRequired, "surfaceStorage[0].surfaceId")]
    [InlineData("digest", MapDocumentRefusal.SchemaValueRequired, "surfaceStorage[0].sha256")]
    [InlineData("covers", MapDocumentRefusal.SchemaObjectRequired, "surfaceStorage[0].covers")]
    [InlineData("cover-count", MapDocumentRefusal.SchemaArrayLengthMismatch, "surfaceStorage[0].covers")]
    [InlineData("cover-value", MapDocumentRefusal.SchemaValueRequired, "surfaceStorage[0].covers.minX")]
    public void DirectoryReferences_SelectTheFirstFailedShapeRule(string fault, MapDocumentRefusal reason, string field)
    {
        var page = new MapDirectoryPageRef("ground", new(0, 0, 1, 1), new string('a', 64));
        var options = MapDocumentFile.CreateCompactOptions(MapDocRegistry.CreateDefault());
        var value = JsonSerializer.SerializeToNode(page, options)!.AsObject();
        var root = new JsonObject { ["surfaceStorage"] = new JsonArray(value) };
        switch (fault)
        {
            case "array": root["surfaceStorage"] = new JsonObject(); break;
            case "item": root["surfaceStorage"]![0] = null; break;
            case "count": value["extra"] = 0; break;
            case "surface": value["surfaceId"] = null; break;
            case "digest": value["sha256"] = null; break;
            case "covers": value["covers"] = new JsonArray(); break;
            case "cover-count": value["covers"]!["extra"] = 0; break;
            case "cover-value": value["covers"]!["minX"] = null; break;
        }
        var error = Assert.Throws<MapDocumentException>(() => MapSurfaceTiledStore.ReadDirectory(root));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        if (fault != "array") Assert.Equal(0, error.Index);
        if (fault is "count" or "cover-count")
        {
            Assert.Equal(fault == "count" ? 4 : 5, error.ActualCount);
            Assert.Equal(fault == "count" ? 3 : 4, error.ExpectedCount);
        }
        var control = new JsonObject { ["surfaceStorage"] = JsonSerializer.SerializeToNode(new[] { page }, options) };
        Assert.Equal(page, Assert.Single(MapSurfaceTiledStore.ReadDirectory(control)));
    }

}
