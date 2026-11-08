using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Markers;

public sealed class MapMarkerMigrationRefusalTests
{
    [Theory]
    [InlineData(null, MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("null", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("\"7\"", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("6", MapDocumentRefusal.DocumentMigrationSourceVersionInvalid)]
    [InlineData("9", MapDocumentRefusal.DocumentMigrationSourceVersionInvalid)]
    public void SourceVersion_ReportsTheFailedBranchAndLeavesSourceUnchanged(string? value, MapDocumentRefusal reason)
    {
        var source = new JsonObject();
        if (value is not null) source["formatVersion"] = JsonNode.Parse(value);
        string before = source.ToJsonString();
        var error = Assert.Throws<MapDocumentException>(() => MapMarkerMigration.Upgrade(source));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("formatVersion", error.FieldPath);
        Assert.Equal(value is "6" or "9" ? int.Parse(value) : (int?)null, error.ActualVersion);
        Assert.Equal(new[] { 7, 8 }, error.SupportedVersions);
        Assert.Equal(before, source.ToJsonString());
        source["formatVersion"] = 7;
        Assert.Equal(8, MapMarkerMigration.Upgrade(source)["formatVersion"]!.GetValue<int>());
        source["formatVersion"] = 8;
        Assert.Equal(8, MapMarkerMigration.Upgrade(source)["formatVersion"]!.GetValue<int>());
    }
}
