using System;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceneFullPath_SubToleranceFeatherTranslationPasses(bool reorder)
    {
        using var fixture = ShippedSceneGuardFixtures.Create("terrain-feather-origin");
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        var legacy = LegacySceneRecorder.Record(inputs.World, inputs.Catalogs, inputs);
        var feather = legacy.Ground.SelectMany(d => d.Mesh.Indices32.Select(i => d.Mesh.Vertices[i]))
            .Where(v => v.Tangent.W > 0).ToArray();
        Assert.Equal(2 * 16 * 16 * 3, feather.Length);
        // Source lattice points cover the outer feather boundary and a painted canonical shared edge.
        Assert.Contains(feather, v => v.Position == new Vector3(0, 0, -.25f));
        Assert.Contains(feather, v => v.Position == new Vector3(.0625f, 0, -.0625f));
        int changes = 0;
        PrivateOracleEntry.Run(fixture.Environment, (_, report) => ShippedSceneComparison.Compare(inputs, report, observation =>
        {
            changes++;
            if (reorder) observation = ShippedSceneGuardTerrainPaint.Reorder(observation);
            var shift = Matrix4x4.CreateTranslation(MathF.ScaleB(1, -25), 0, 0);
            return observation with
            {
                DrawSet = new(observation.DrawSet.Resources, observation.DrawSet.Draws.Select(d =>
                    d.Resource.Key.Kind == MapRenderKind.Terrain ? d with { LocalTransform = d.LocalTransform * shift } : d).ToArray())
            };
        }));
        Assert.True(changes > 0);
        Assert.Empty(fixture.FailureCategories());
    }
}
