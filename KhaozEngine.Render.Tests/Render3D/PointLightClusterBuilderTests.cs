using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Pure coverage of conservative CPU point-light clustering. Expected cluster coordinates come from
/// simple analytic ortho cells or independently projected sample points, not from the builder's plane tests.</summary>
[Collection("AllocSensitive")]
public sealed class PointLightClusterBuilderTests
{
    static readonly Matrix4x4 Ortho = Matrix4x4.CreateOrthographic(32f, 18f, 1f, 25f);
    static readonly Matrix4x4 Perspective =
        Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 1f, 1f, 256f);
    static readonly Vector3 Forward = -Vector3.UnitZ;

    [Theory]
    [InlineData(3f, 0f, -1.5f, 1f)]
    [InlineData(3f, 2f, -1.5f, 1.414214f)]
    [InlineData(3f, 2f, -3f, 1.732051f)]
    public void SpheresTouchingAClusterFaceEdgeOrCornerAreKept(float x, float y, float z, float radius)
    {
        var builder = new PointLightClusterBuilder();
        ModelRenderer.PointLightData[] lights = [Light(new Vector3(x, y, z), radius)];

        builder.Build(lights, Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);

        AssertContains(builder, x: 8, y: 4, z: 0, light: 0);
    }

    [Fact]
    public void SamplePointsInsideLightInfluenceAlwaysFindThatLightInTheirAnalyticOrthoCluster()
    {
        var builder = new PointLightClusterBuilder();
        ModelRenderer.PointLightData[] lights =
        [
            Light(new Vector3(1f, 0.5f, -1.5f), 0.6f),
            Light(new Vector3(-15f, -8.5f, -24.5f), 0.6f),
            Light(new Vector3(15f, 8f, -12.5f), 0.8f),
        ];

        builder.Build(lights, Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);

        // Samples (0.5,0.5,-1.5), (-15.5,-8.5,-24.5), and (15.5,8.5,-12.5) are inside the
        // corresponding spheres. The 32 by 18 by 24 ortho frustum makes each cell exactly 2 by 2 by 1.
        AssertContains(builder, 8, 4, 0, 0);
        AssertContains(builder, 0, 0, 23, 1);
        AssertContains(builder, 15, 8, 11, 2);
    }

    [Fact]
    public void PerspectiveSphereWhoseCenterIsBehindTheNearPlaneStillReachesTheFirstSlice()
    {
        var builder = new PointLightClusterBuilder();
        ModelRenderer.PointLightData[] lights = [Light(new Vector3(0f, 0f, -0.5f), 0.6f)];

        builder.Build(lights, Perspective, Vector3.Zero, Forward, Perspective, Vector3.Zero);

        Assert.Equal(1f, builder.Depth.W);
        AssertContains(builder, 8, 4, 0, 0);
    }

    [Fact]
    public void PerspectiveLogSliceBoundaryKeepsATouchingSphereOnBothSides()
    {
        var builder = new PointLightClusterBuilder();
        // 256 far over 1 near across 24 slices makes the first boundary cube-root(2).
        const float FirstBoundary = 1.25992105f;
        ModelRenderer.PointLightData[] lights =
            [Light(new Vector3(0f, 0f, -FirstBoundary), 0.001f)];

        builder.Build(lights, Perspective, Vector3.Zero, Forward, Perspective, Vector3.Zero);

        AssertContains(builder, 8, 4, 0, 0);
        AssertContains(builder, 8, 4, 1, 0);
        Assert.Equal(MathF.Log(256f), builder.Depth.Z, 4);
    }

    [Fact]
    public void OrthographicNegativeNearPlaneUsesFiniteLinearSlices()
    {
        Matrix4x4 projection = Matrix4x4.CreateOrthographic(32f, 18f, -2f, 22f);
        var builder = new PointLightClusterBuilder();
        ModelRenderer.PointLightData[] lights = [Light(new Vector3(0f, 0f, 1f), 0.01f)];

        builder.Build(lights, projection, Vector3.Zero, Forward, projection, Vector3.Zero);

        Assert.Equal(-2f, builder.Depth.X, 4);
        Assert.Equal(22f, builder.Depth.Y, 4);
        Assert.Equal(0f, builder.Depth.W);
        AssertContains(builder, 8, 4, 0, 0);
        AssertContains(builder, 8, 4, 1, 0);
    }

    [Fact]
    public void CorrectedClipYControlsTheClusterRow()
    {
        var regular = new PointLightClusterBuilder();
        var flipped = new PointLightClusterBuilder();
        Matrix4x4 corrected = Ortho * Matrix4x4.CreateScale(1f, -1f, 1f);
        ModelRenderer.PointLightData[] lights = [Light(new Vector3(0f, 6f, -1.5f), 0.1f)];

        regular.Build(lights, Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);
        flipped.Build(lights, corrected, Vector3.Zero, Forward, Ortho, Vector3.Zero);

        AssertContains(regular, 8, 7, 0, 0);
        AssertContains(flipped, 8, 1, 0, 0);
        Assert.False(ClusterContains(flipped, 8, 7, 0, 0));
    }

    [Fact]
    public void FloatingOriginShiftLeavesEveryPackedIndexUnchanged()
    {
        var atZero = new PointLightClusterBuilder();
        var shifted = new PointLightClusterBuilder();
        var origin = new Vector3(100_000f, -50_000f, 70_000f);
        ModelRenderer.PointLightData[] local =
        [
            Light(new Vector3(2f, 3f, -10f), 2f),
            Light(new Vector3(-8f, -4f, -20f), 3f),
        ];
        ModelRenderer.PointLightData[] absolute =
        [
            Light(origin + new Vector3(2f, 3f, -10f), 2f),
            Light(origin + new Vector3(-8f, -4f, -20f), 3f),
        ];

        atZero.Build(local, Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);
        shifted.Build(absolute, Ortho, Vector3.Zero, Forward, Ortho, origin);

        Assert.Equal(atZero.Depth, shifted.Depth);
        Assert.Equal(atZero.CameraForward, shifted.CameraForward);
        Assert.Equal(atZero.Image, shifted.Image);
    }

    [Fact]
    public void OffAxisObliqueProjectionKeepsAProjectedSampleInfluence()
    {
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveOffCenter(-0.5f, 1.5f, -1f, 1f, 1f, 80f);
        // Keep the oblique terms below the projection's far coefficient margin. Larger arbitrary terms make some
        // z=1 corner rays cross infinity and describe a malformed frustum rather than a tilted valid one.
        projection.M13 = 0.002f;
        projection.M23 = -0.001f;
        var builder = new PointLightClusterBuilder();
        Vector3 sample = new(3f, -1f, -12f);
        ModelRenderer.PointLightData[] lights = [Light(sample + new Vector3(0.2f, 0f, 0f), 0.25f)];

        builder.Build(lights, projection, Vector3.Zero, Forward, projection, Vector3.Zero);

        Assert.NotEqual(-1f, builder.Depth.W);
        Vector4 clip = Vector4.Transform(new Vector4(sample, 1f), projection);
        Vector3 ndc = new(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
        int x = Math.Clamp((int)((ndc.X + 1f) * 8f), 0, 15);
        int y = Math.Clamp((int)((ndc.Y + 1f) * 4.5f), 0, 8);
        int z = PerspectiveSlice(Vector3.Dot(sample, Forward), builder.Depth);
        AssertContains(builder, x, y, z, 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void InvalidCameraDataMarksEveryClusterForFullListFallback(int invalidKind)
    {
        Matrix4x4 vp = Ortho;
        Matrix4x4 projection = Ortho;
        Vector3 forward = Forward;
        if (invalidKind == 0) vp = default;
        if (invalidKind == 1) vp.M11 = float.NaN;
        if (invalidKind == 2) forward = Vector3.Zero;
        if (invalidKind == 3) projection = default;
        if (invalidKind == 4) vp = Matrix4x4.Identity;
        var builder = new PointLightClusterBuilder();

        builder.Build([Light(new Vector3(0f, 0f, -2f), 1f)], vp, Vector3.Zero, forward,
            projection, Vector3.Zero);

        Assert.Equal(-1f, builder.Depth.W);
        Assert.True(float.IsFinite(builder.Depth.X));
        Assert.True(float.IsFinite(builder.Depth.Y));
        Assert.True(float.IsFinite(builder.Depth.Z));
        Assert.Equal(PointLightClusterBuilder.ClusterCount, builder.OverflowedClusters);
        Assert.Equal(0, builder.LightReferenceCount);
        for (int cluster = 0; cluster < PointLightClusterBuilder.ClusterCount; cluster++)
        {
            int offset = cluster * PointLightClusterBuilder.ClusterStrideUInts;
            Assert.Equal(0u, builder.Image[offset]);
            Assert.Equal(1u, builder.Image[offset + 1]);
        }
    }

    [Fact]
    public void SixtyFifthReferenceSetsOverflowAndKeepsStableSubmittedIndices()
    {
        var builder = new PointLightClusterBuilder();
        var lights = new ModelRenderer.PointLightData[65];
        for (int i = 0; i < lights.Length; i++) lights[i] = Light(new Vector3(1f, 0f, -1.5f), 0.1f);

        builder.Build(lights, Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);

        int offset = ClusterOffset(8, 4, 0);
        Assert.Equal(64u, builder.Image[offset]);
        Assert.Equal(1u, builder.Image[offset + 1]);
        for (uint i = 0; i < 64; i++) Assert.Equal(i, builder.Image[offset + 4 + i]);
        Assert.True(builder.OverflowedClusters > 0);
        Assert.True(builder.LightReferenceCount >= 64);
    }

    [Fact]
    public void WarmBuildAllocatesNothing()
    {
        var builder = new PointLightClusterBuilder();
        ModelRenderer.PointLightData[] lights =
        [
            Light(new Vector3(0f, 0f, -2f), 1f),
            Light(new Vector3(4f, 2f, -12f), 2f),
        ];
        for (int i = 0; i < 4; i++)
            builder.Build(lights, Perspective, Vector3.Zero, Forward, Perspective, Vector3.Zero);

        AllocAssert.NoPerCallAllocation("PointLightClusterBuilder.Build over 8 warmed calls", () =>
        {
            for (int i = 0; i < 8; i++)
                builder.Build(lights, Perspective, Vector3.Zero, Forward, Perspective, Vector3.Zero);
        });
    }

    static ModelRenderer.PointLightData Light(Vector3 position, float radius) => new()
    {
        PosRadius = new Vector4(position, radius),
        ColorIntensity = Vector4.One,
    };

    static void AssertContains(PointLightClusterBuilder builder, int x, int y, int z, uint light) =>
        Assert.True(ClusterContains(builder, x, y, z, light),
            $"cluster ({x},{y},{z}) did not contain submitted light {light}");

    static bool ClusterContains(PointLightClusterBuilder builder, int x, int y, int z, uint light)
    {
        foreach (uint candidate in ClusterIndices(builder, x, y, z))
            if (candidate == light) return true;
        return false;
    }

    static ReadOnlySpan<uint> ClusterIndices(PointLightClusterBuilder builder, int x, int y, int z)
    {
        int offset = ClusterOffset(x, y, z);
        int count = (int)builder.Image[offset];
        return builder.Image.AsSpan(offset + PointLightClusterBuilder.HeaderUInts, count);
    }

    static int ClusterOffset(int x, int y, int z) =>
        ((z * PointLightClusterBuilder.ClusterCountY + y) * PointLightClusterBuilder.ClusterCountX + x)
        * PointLightClusterBuilder.ClusterStrideUInts;

    static int PerspectiveSlice(float depth, Vector4 depthParams)
    {
        float normalized = MathF.Log(depth / depthParams.X) / depthParams.Z;
        return Math.Clamp((int)(normalized * PointLightClusterBuilder.ClusterCountZ), 0,
            PointLightClusterBuilder.ClusterCountZ - 1);
    }
}
