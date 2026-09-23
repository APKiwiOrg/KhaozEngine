using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Pins that the cluster builder tests only the clusters a light's conservative range reaches (issue #1112).
/// <see cref="PointLightClusterEquivalenceTests"/> proves the result did not change. These prove the work did.</summary>
public sealed class PointLightClusterRangeTests
{
    static readonly Matrix4x4 Ortho = Matrix4x4.CreateOrthographic(32f, 18f, 1f, 25f);
    static readonly Matrix4x4 TinyNear = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 1f, 1e-5f, 256f);
    static readonly Vector3 Forward = -Vector3.UnitZ;

    [Fact]
    public void ASmallLightInViewBuildsAndTestsOnlyItsOwnCluster()
    {
        var builder = new PointLightClusterBuilder();
        ModelRenderer.PointLightData[] lights = [Light(new Vector3(1f, 0.5f, -1.5f), 0.1f)];

        builder.Build(lights, Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);

        // The 32 by 18 by 24 ortho frustum makes each cell 2 by 2 by 1, and this sphere sits well inside (8, 4, 0).
        // The brute-force builder built 3,456 plane sets and ran 3,456 sphere tests for it.
        Assert.Equal(1, builder.PlaneSetsBuilt);
        Assert.Equal(1, builder.SphereTests);
        Assert.Equal(1, builder.LightReferenceCount);
    }

    [Theory]
    [InlineData(100f, 0f, -10f)]
    [InlineData(0f, 0f, 40f)]
    [InlineData(0f, 0f, -60f)]
    public void ALightWhollyOutsideTheFrustumIsCulledBeforeAnyClusterIsBuilt(float x, float y, float z)
    {
        var builder = new PointLightClusterBuilder();
        ModelRenderer.PointLightData[] lights = [Light(new Vector3(x, y, z), 1f)];

        builder.Build(lights, Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);

        Assert.Equal(0f, builder.Depth.W);
        Assert.Equal(0, builder.PlaneSetsBuilt);
        Assert.Equal(0, builder.SphereTests);
        Assert.Equal(0, builder.LightReferenceCount);
    }

    [Fact]
    public void ADegenerateClusterThatNoLightReachesNoLongerForcesTheFullListFallback()
    {
        // A 10 micrometre near plane makes every first-slice cluster too thin for a plane. The brute-force builder built
        // them all and fell back to the full list. The range-limited builder never builds them for a light 50 m away,
        // so the frame stays clustered. Lighting is the same either way, because the fallback walks every light and the
        // shader skips a light outside its radius. This is the documented spec conflict, pinned so it can be reviewed.
        ModelRenderer.PointLightData[] lights = [Light(new Vector3(0f, 0f, -50f), 1f)];
        var oracle = new PointLightClusterOracle();
        var builder = new PointLightClusterBuilder();

        oracle.Build(lights, TinyNear, Vector3.Zero, Forward, TinyNear, Vector3.Zero);
        builder.Build(lights, TinyNear, Vector3.Zero, Forward, TinyNear, Vector3.Zero);

        Assert.Equal(-1f, oracle.Depth.W);
        Assert.Equal(1f, builder.Depth.W);
        Assert.Equal(0, builder.OverflowedClusters);
        int slice = Math.Clamp((int)(MathF.Log(50f / builder.Depth.X) / builder.Depth.Z
            * PointLightClusterBuilder.ClusterCountZ), 0, PointLightClusterBuilder.ClusterCountZ - 1);
        Assert.True(PointLightClusterImage.Contains(builder, 8, 4, slice, 0u),
            $"cluster (8, 4, {slice}) did not contain the light 50 m ahead");
    }

    [Fact]
    public void ADegenerateClusterALightReachesStillForcesTheFullListFallback()
    {
        ModelRenderer.PointLightData[] lights = [Light(new Vector3(0f, 0f, -1f), 2f)];
        var oracle = new PointLightClusterOracle();
        var builder = new PointLightClusterBuilder();

        oracle.Build(lights, TinyNear, Vector3.Zero, Forward, TinyNear, Vector3.Zero);
        builder.Build(lights, TinyNear, Vector3.Zero, Forward, TinyNear, Vector3.Zero);

        Assert.Equal(-1f, oracle.Depth.W);
        PointLightClusterImage.AssertSameAssignment(oracle, builder, "a light reaching the degenerate first slice");
    }

    static ModelRenderer.PointLightData Light(Vector3 position, float radius) => new()
    {
        PosRadius = new Vector4(position, radius),
        ColorIntensity = Vector4.One,
    };
}
