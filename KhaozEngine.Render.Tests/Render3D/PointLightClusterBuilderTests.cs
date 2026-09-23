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
        Assert.Equal(atZero.UsedUIntCount, shifted.UsedUIntCount);
        Assert.Equal(atZero.Image[..atZero.UsedUIntCount], shifted.Image[..shifted.UsedUIntCount]);
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
        Assert.Equal(PointLightClusterBuilder.HeaderRegionUInts, builder.UsedUIntCount);
        for (int cluster = 0; cluster < PointLightClusterBuilder.ClusterCount; cluster++)
            Assert.Equal(PointLightClusterBuilder.OverflowCount, builder.Image[cluster]);
    }

    [Fact]
    public void SixtyFifthReferenceMarksOverflowAndStoresNoIndices()
    {
        var builder = new PointLightClusterBuilder();

        builder.Build(Stack(65, new Vector3(1f, 0f, -1.5f), 0.1f), Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);

        Assert.Equal(PointLightClusterBuilder.OverflowCount, builder.Image[PointLightClusterImage.ClusterIndex(8, 4, 0)]);
        Assert.Equal(1, builder.OverflowedClusters);
        // The diagnostic keeps its meaning: the 64 references the cluster accepted before it overflowed.
        Assert.Equal(64, builder.LightReferenceCount);
        Assert.Equal(PointLightClusterBuilder.HeaderRegionUInts, builder.UsedUIntCount);
    }

    [Fact]
    public void SixtyFourReferencesFillAClusterInAscendingOrderWithoutOverflow()
    {
        var builder = new PointLightClusterBuilder();

        builder.Build(Stack(64, new Vector3(1f, 0f, -1.5f), 0.1f), Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);

        ReadOnlySpan<uint> stored = PointLightClusterImage.Lights(builder, PointLightClusterImage.ClusterIndex(8, 4, 0),
            out bool overflow);
        Assert.False(overflow);
        Assert.Equal(64, stored.Length);
        for (int i = 0; i < stored.Length; i++) Assert.Equal((uint)i, stored[i]);
        Assert.Equal(0, builder.OverflowedClusters);
    }

    [Fact]
    public void TheCompactImageKeepsTheBufferSizeAndHoldsAFullGrid()
    {
        Assert.Equal(940_032, PointLightClusterBuilder.ImageUIntCount * sizeof(uint));
        Assert.Equal(864, PointLightClusterBuilder.HeaderRegionUvec4s);
        Assert.Equal(231_552, PointLightClusterBuilder.IndexRegionUInts);
        Assert.True(PointLightClusterBuilder.IndexRegionUInts
            >= PointLightClusterBuilder.ClusterCount * PointLightClusterBuilder.MaxLightsPerCluster);
        Assert.True(PointLightClusterBuilder.IndexRegionUInts < 1 << (32 - PointLightClusterBuilder.CountBits));
        Assert.True(PointLightClusterBuilder.MaxLightsPerCluster < PointLightClusterBuilder.OverflowCount);
    }

    [Fact]
    public void SixtyFourLightsReachingEveryClusterFillTheIndexRegionWithoutRunningOut()
    {
        var builder = new PointLightClusterBuilder();

        builder.Build(Stack(64, new Vector3(0f, 0f, -13f), 100f), Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);

        Assert.Equal(0, builder.OverflowedClusters);
        Assert.Equal(PointLightClusterBuilder.ClusterCount * 64, builder.LightReferenceCount);
        Assert.Equal(PointLightClusterBuilder.HeaderRegionUInts + PointLightClusterBuilder.ClusterCount * 64,
            builder.UsedUIntCount);
        for (int cluster = 0; cluster < PointLightClusterBuilder.ClusterCount; cluster++)
        {
            Assert.Equal(((uint)(cluster * 64) << PointLightClusterBuilder.CountBits) | 64u, builder.Image[cluster]);
            ReadOnlySpan<uint> stored = PointLightClusterImage.Lights(builder, cluster, out _);
            for (int i = 0; i < 64; i++) Assert.Equal((uint)i, stored[i]);
        }
    }

    [Fact]
    public void SixtyFiveLightsReachingEveryClusterOverflowEveryHeaderAndStoreNoIndices()
    {
        var builder = new PointLightClusterBuilder();

        builder.Build(Stack(65, new Vector3(0f, 0f, -13f), 100f), Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);

        Assert.Equal(0f, builder.Depth.W);
        Assert.Equal(PointLightClusterBuilder.ClusterCount, builder.OverflowedClusters);
        Assert.Equal(PointLightClusterBuilder.ClusterCount * 64, builder.LightReferenceCount);
        Assert.Equal(PointLightClusterBuilder.HeaderRegionUInts, builder.UsedUIntCount);
        for (int cluster = 0; cluster < PointLightClusterBuilder.ClusterCount; cluster++)
            Assert.Equal(PointLightClusterBuilder.OverflowCount, builder.Image[cluster]);
    }

    [Fact]
    public void ClusterIndicesAreContiguousInClusterOrderAndThePaddingIsZero()
    {
        var builder = new PointLightClusterBuilder();
        ModelRenderer.PointLightData[] lights =
        [
            Light(new Vector3(0f, 0f, -2f), 1f),
            Light(new Vector3(4f, 2f, -12f), 2f),
            Light(new Vector3(-3f, -1f, -6f), 1.5f),
            // The first three store 440 indices, a whole uvec4. This one sits well inside a single cluster and leaves
            // a three-uint pad.
            Light(new Vector3(1f, 0.5f, -3f), 0.01f),
        ];

        // A dense frame first stores indices 0 to 63 in every cluster, so index slot k of the region holds k mod 64.
        // The pad below starts past a count that is not a multiple of four, so every slot it covers holds a non-zero
        // stale index and only the build's own zeroing clears it.
        builder.Build(Stack(64, new Vector3(0f, 0f, -13f), 100f), Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);
        Assert.Equal(0, builder.OverflowedClusters);
        Assert.Equal(PointLightClusterBuilder.HeaderRegionUInts + PointLightClusterBuilder.ClusterCount * 64,
            builder.UsedUIntCount);
        builder.Build(lights, Perspective, Vector3.Zero, Forward, Perspective, Vector3.Zero);

        int next = 0;
        for (int cluster = 0; cluster < PointLightClusterBuilder.ClusterCount; cluster++)
        {
            uint header = builder.Image[cluster];
            uint count = header & PointLightClusterBuilder.CountMask;
            if (count == 0u)
            {
                Assert.Equal(0u, header);
                continue;
            }
            Assert.NotEqual(PointLightClusterBuilder.OverflowCount, count);
            Assert.Equal((uint)next, header >> PointLightClusterBuilder.CountBits);
            ReadOnlySpan<uint> stored = PointLightClusterImage.Lights(builder, cluster, out _);
            for (int i = 1; i < stored.Length; i++) Assert.True(stored[i - 1] < stored[i]);
            next += (int)count;
        }
        int used = PointLightClusterBuilder.HeaderRegionUInts + next;
        Assert.True(next > 0);
        Assert.True(next % 4 != 0, $"the frame stored {next} indices, a whole uvec4, so it leaves no pad to check");
        Assert.Equal((used + 3) & ~3, builder.UsedUIntCount);
        for (int i = used; i < builder.UsedUIntCount; i++) Assert.Equal(0u, builder.Image[i]);
    }

    [Fact]
    public void ASparseFrameAfterADenseOneMatchesAFreshBuilderOverItsUsedPrefix()
    {
        var reused = new PointLightClusterBuilder();
        var fresh = new PointLightClusterBuilder();
        ModelRenderer.PointLightData[] sparse =
        [
            Light(new Vector3(1f, 0.5f, -1.5f), 0.6f),
            Light(new Vector3(-6f, 3f, -12f), 3f),
        ];

        reused.Build(Stack(65, new Vector3(0f, 0f, -13f), 100f), Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);
        reused.Build(sparse, Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);
        fresh.Build(sparse, Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);

        Assert.Equal(fresh.UsedUIntCount, reused.UsedUIntCount);
        Assert.Equal(fresh.Image[..fresh.UsedUIntCount], reused.Image[..reused.UsedUIntCount]);
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

    static bool ClusterContains(PointLightClusterBuilder builder, int x, int y, int z, uint light) =>
        PointLightClusterImage.Contains(builder, x, y, z, light);

    static ModelRenderer.PointLightData[] Stack(int count, Vector3 position, float radius)
    {
        var lights = new ModelRenderer.PointLightData[count];
        for (int i = 0; i < count; i++) lights[i] = Light(position, radius);
        return lights;
    }

    static int PerspectiveSlice(float depth, Vector4 depthParams)
    {
        float normalized = MathF.Log(depth / depthParams.X) / depthParams.Z;
        return Math.Clamp((int)(normalized * PointLightClusterBuilder.ClusterCountZ), 0,
            PointLightClusterBuilder.ClusterCountZ - 1);
    }
}
