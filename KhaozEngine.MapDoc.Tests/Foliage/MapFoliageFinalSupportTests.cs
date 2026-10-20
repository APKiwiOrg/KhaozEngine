using System;
using KhaozEngine.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageFinalSupportTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ContainingSolid_WinsOverRoofRegardlessOfOrderAndIndexLevel(bool solidFirst, bool wideSolid)
    {
        var fixture = FoliageFixtures.Clearance("mixed");
        foreach (var placement in fixture.Document.Placements) { placement.X = 0; placement.Z = 0; }
        var solid = new MapFoliageMask(solidFirst ? "a-solid" : "z-solid", FoliageFixtures.Owner("peer"), FoliageFixtures.Surface("floor-1"),
            MapFoliageClearanceKind.Solid, wideSolid ? new(-256, -256, 256, 256) : new(-1, -1, 1, 1));
        var roof = new MapFoliageMask(solidFirst ? "z-roof" : "a-roof", FoliageFixtures.Owner("higher"), FoliageFixtures.Surface("floor-1"),
            MapFoliageClearanceKind.RoofAbove, new(-1, -1, 1, 1));
        FoliageFixtures.WithClearance(fixture, layer => FoliageFixtures.ClearanceLayer(layer,
            rules: [], masks: solidFirst ? [solid, roof] : [roof, solid]));

        Assert.Equal(MapFoliageReason.Solid, MapFoliageClearanceTests.Sample(fixture, 0.1f, 0.1f).Reason);
    }

    [Fact]
    public void InBoundsUnrepresentableCoordinate_ReturnsTypedRefusal()
    {
        var fixture = FoliageFixtures.Create();
        var context = FoliageFixtures.Context(fixture);
        var prepared = MapFoliageSupport.Prepare(context, context.Foliage!.Layers[0], new(0, 0), new());
        Assert.Equal(MapFoliageStatus.Complete, prepared.Status);

        var point = prepared.Sample(MathF.ScaleB(1, -64), 0.5f);

        Assert.Equal((MapFoliageStatus.NotRepresentable, MapFoliageReason.Refused), (point.Status, point.Reason));
    }
}
