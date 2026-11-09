using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

public sealed partial class ShippedSceneInputGuardTests
{
    [Theory]
    [InlineData("valid", "terrain-height", "scene-terrain-geometry")]
    [InlineData("water-valid", "water-height", "scene-water-geometry")]
    public void SceneFullPath_RejectsPayloadChangesInWindowWithoutProbe(string variant, string mutation, string category)
    {
        using var fixture = ShippedSceneGuardFixtures.Create(variant);
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        Assert.DoesNotContain(inputs.Manifest.Policy.Probes, p => p.WindowId == "B");
        int changed = 0;
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment, (_, report) =>
            ShippedSceneComparison.Compare(inputs, report, observePlan: (window, draws) =>
            {
                if (window != "B") return draws;
                changed++;
                return ShippedSceneGuardObservations.Mutate(new(draws, [], []), mutation).DrawSet;
            })));
        Assert.True(changed > 0);
        Assert.Contains(category, fixture.FailureCategories());
    }
}
