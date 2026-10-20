using System.Text.Json.Nodes;
using KhaozEngine.Content;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Foliage;
using KhaozEngine.Tests.MapDoc.Markers;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class FoliageFinalMinorTests
{
    [Fact]
    public void Union_PreservesAdoptionOfLegacySpawnsInBothOrders()
    {
        var fixture = MarkerFixtures.LegacyLists();
        var adopted = NativeDocumentTransaction.Stage(fixture.Document, new AdoptSpawnsCommand(MarkerFixtures.Choices()), false, fixture.Assets);
        Assert.True(adopted.WriteSet.LegacySpawns);
        foreach (var combined in new[] { MapNativeWriteSet.Union(adopted.WriteSet, MapNativeWriteSet.PlacementsOnly),
            MapNativeWriteSet.Union(MapNativeWriteSet.PlacementsOnly, adopted.WriteSet) })
        {
            Assert.True(combined.LegacySpawns);
            Assert.True(combined.BuildingIntent);
            Assert.True(combined.Placements);
        }
    }

    [Theory]
    [InlineData("3.4028235e38", true)]
    [InlineData("3.4028235677973366e38", false)]
    public void CellSizeSchema_UsesTheExclusiveBinary32RoundingBoundary(string number, bool accepted)
    {
        var root = JsonNode.Parse(MapDocumentFile.SaveText(FoliageFixtures.Create().Document))!;
        root["foliage"]!["layers"]![0]!["cellSize"] = JsonNode.Parse(number);
        Assert.Equal(accepted, JsonSchemaValidator.Validate(root.ToJsonString(), MapDocumentSchema.GetJson()).IsValid);
    }
}
