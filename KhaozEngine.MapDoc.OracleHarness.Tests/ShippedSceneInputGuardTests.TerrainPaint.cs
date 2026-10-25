using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

public sealed partial class ShippedSceneInputGuardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceneFullPath_RejectsRemovedFeatherRefinement(bool reorder)
    {
        using var fixture = ShippedSceneGuardFixtures.Create("terrain-painted");
        int changes = 0;
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment, (inputs, report) =>
            ShippedSceneComparison.Compare(ShippedSceneInputs.Read(inputs), report, observation =>
            {
                changes++;
                return ShippedSceneGuardTerrainPaint.Simplify(observation, reorder);
            })));
        Assert.True(changes > 0);
        Assert.Contains("scene-terrain-paint", fixture.FailureCategories());
        Assert.DoesNotContain("scene-terrain-geometry", fixture.FailureCategories());
    }

    [Fact]
    public void SceneFullPath_ReorderedFeatherTrianglesPass()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("terrain-painted");
        PrivateOracleEntry.Run(fixture.Environment, (inputs, report) => ShippedSceneComparison.Compare(
            ShippedSceneInputs.Read(inputs), report, ShippedSceneGuardTerrainPaint.Reorder));
        Assert.Empty(fixture.FailureCategories());
    }
}
