using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Spaces;

/// <summary>Boundary coverage tests each column only against the columns whose bounds meet it.</summary>
public sealed class SpaceBoundaryCoverageWorkTests
{
    [Fact]
    public void TransformedRoomAndWater_TestEachColumnAgainstNearbyColumnsOnly()
    {
        var view = PrefabFixtures.Effective(PrefabFixtures.Transformed(0.371f, 1.2f, new(30000.25f, -450, -29999.75f))).CompleteView();
        long cells = MapSpaceGeometry.Records(view).OfType<MapSpaceFootprint>().Sum(f => f.SlotCells.Count);
        // Scanning every column for each of four edges made 4 n (n - 1) tests.
        Assert.True(cells > 100);
        long before = MapSpaceBoundaryCoverage.ThreadNeighbourTests;

        Assert.Empty(MapSpaceCoverageValidator.Validate(view));

        // Each edge of a cell column meets at most the eight columns around it.
        Assert.InRange(MapSpaceBoundaryCoverage.ThreadNeighbourTests - before, 1, 4 * 8 * cells);
    }
}
