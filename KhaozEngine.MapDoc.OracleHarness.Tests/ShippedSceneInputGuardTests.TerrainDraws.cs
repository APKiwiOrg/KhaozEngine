using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

public sealed partial class ShippedSceneInputGuardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceneFullPath_RejectsExtraTerrainDrawInWindowWithoutProbe(bool displaced)
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid");
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        Assert.DoesNotContain(inputs.Manifest.Policy.Probes, p => p.WindowId == "B");
        int changed = 0;
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment, (_, report) =>
            ShippedSceneComparison.Compare(inputs, report, observePlan: (window, draws) =>
            {
                if (window != "B") return draws;
                changed++;
                var original = draws.Draws.First(d => d.Resource.Key.Kind == MapRenderKind.Terrain);
                var extra = displaced ? original with { LocalTransform = original.LocalTransform * Matrix4x4.CreateTranslation(Vector3.UnitY) } : original;
                return new(draws.Resources, draws.Draws.Append(extra).ToArray());
            })));
        Assert.True(changed > 0);
        Assert.Contains("scene-terrain-frame", fixture.FailureCategories());
        if (displaced) Assert.Contains("scene-terrain-geometry", fixture.FailureCategories());
    }
}
