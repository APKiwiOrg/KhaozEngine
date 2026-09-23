using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Pins that the cluster builder tests only the clusters a light's conservative range reaches (issue #1112).
/// <see cref="PointLightClusterEquivalenceTests"/> proves on seeded scenes that the result did not change. These prove
/// the work did, and pin the one known difference, which adds no light.</summary>
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

    [Fact]
    public void AHugeLightTheBruteForceAdmitsOnlyThroughSliceZeroRoundingIsDroppedAndCannotReachIt()
    {
        // The eye sits kilometres from render-space zero, so slice-zero clusters, about a centimetre across here, are
        // built from corners whose float rounding is about a percent of their own size. Their planes tilt, and at this huge
        // light's lateral distance the tilt lets the brute force admit it into slice zero. In exact geometry the whole
        // sphere lies nearer than the near plane, mostly behind the eye, so it reaches no cluster and adds no light.
        // The range builder drops it. This is the documented difference, pinned so the guarantee stays honest.
        const float near = 0.1f;
        var eye = new Vector3(1837.1f, 31.3f, -1961.9f);
        Vector3 direction = Vector3.Normalize(new Vector3(0.8f, -0.35f, -1.3f));
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(1.1f, 1.7778f, near, 300f);
        Matrix4x4 viewProjection = Matrix4x4.CreateLookAt(eye, eye + direction, Vector3.UnitY) * projection;
        var center = new Vector3(1851.30615f, 6.78155136f, -1827.14478f);
        const float radius = 96.4895401f;
        ModelRenderer.PointLightData[] lights = [Light(center, radius)];
        var oracle = new PointLightClusterOracle();
        var builder = new PointLightClusterBuilder();

        oracle.Build(lights, viewProjection, eye, direction, projection, Vector3.Zero);
        builder.Build(lights, viewProjection, eye, direction, projection, Vector3.Zero);

        Assert.Equal(1f, oracle.Depth.W);
        Assert.Equal(1f, builder.Depth.W);
        Assert.True(oracle.LightReferenceCount > 0, "precondition: the brute force admits the light somewhere");
        Assert.Equal(0, builder.LightReferenceCount);
        Assert.Equal(0, builder.OverflowedClusters);

        // Exact geometry in double precision from the same eye, direction and near value. The far side of the sphere
        // along the normalized forward stays short of the near plane and of the nearest depth the builder slices from.
        double forwardX = 0.8f, forwardY = -0.35f, forwardZ = -1.3f;
        double length = Math.Sqrt(forwardX * forwardX + forwardY * forwardY + forwardZ * forwardZ);
        double depth = (((double)center.X - eye.X) * forwardX + ((double)center.Y - eye.Y) * forwardY
            + ((double)center.Z - eye.Z) * forwardZ) / length;
        double farSide = depth + radius;
        Assert.True(farSide < near, $"the sphere reaches depth {farSide}, past the near plane at {near}");
        Assert.True(farSide < builder.Depth.X,
            $"the sphere reaches depth {farSide}, past the builder's nearest slice depth {builder.Depth.X}");
    }

    static ModelRenderer.PointLightData Light(Vector3 position, float radius) => new()
    {
        PosRadius = new Vector4(position, radius),
        ColorIntensity = Vector4.One,
    };
}
