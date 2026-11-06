using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Water;

public sealed class MapWaterEditRefusalTests
{
    [Theory]
    [InlineData("legacy", MapDocumentRefusal.WaterBodiesPreventLegacyMode)]
    [InlineData("body", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("medium", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("used", MapDocumentRefusal.WaterMediumInUse)]
    [InlineData("bounded", MapDocumentRefusal.WaterBoundedModeRequired)]
    [InlineData("operation", MapDocumentRefusal.OperationKindUnsupported)]
    public void Admission_ReportsTheRuleWithoutChangingWater(string defect, MapDocumentRefusal reason)
    {
        var doc = WaterFixtures.OceanOverCaves();
        var water = doc.Water!;
        MapWaterEdit edit = defect switch
        {
            "legacy" => new MapSetBoundedWater(false),
            "body" => new MapRemoveWaterBody("absent"),
            "medium" => new MapRemoveWaterMedium("absent"),
            "used" => new MapRemoveWaterMedium("sea-water"),
            "bounded" => new MapUpsertWaterBody(water.Bodies[0]),
            _ => new UnsupportedEdit(),
        };
        if (defect == "bounded") doc.Water = null;
        string? before = doc.Water?.Digest();
        var error = Assert.Throws<MapDocumentException>(() => MapWaterEdits.Prepare(doc, edit));
        Assert.Equal(reason, error.Reason);
        if (defect is "body" or "medium") Assert.Equal("absent", error.EntityId);
        if (defect == "used")
        {
            Assert.Equal("sea-water", error.ActualText);
            Assert.Equal("cave-lake", error.EntityId);
        }
        if (defect == "legacy") Assert.Equal(water.Bodies.Count, error.ActualCount);
        if (defect == "operation") Assert.Equal(typeof(UnsupportedEdit).FullName, error.ActualText);
        Assert.Equal(before, doc.Water?.Digest());
        if (defect is "legacy" or "used") water.Bodies.Clear();
        if (defect == "bounded") doc.Water = water;
        if (defect == "body") edit = new MapRemoveWaterBody("ocean");
        if (defect == "medium") edit = new MapRemoveWaterMedium("unused");
        if (defect == "medium") water.Media.Add(water.Media[0] with { Key = "unused" });
        if (defect == "operation") edit = new MapSetBoundedWater(true);
        Assert.NotNull(MapWaterEdits.Prepare(doc, edit));
    }

    sealed record UnsupportedEdit() : MapWaterEdit("unsupported");
}
