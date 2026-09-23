using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class SkinnedPointShadowGoldenTests(SkinnedPointShadowScene fixture)
    : IClassFixture<SkinnedPointShadowScene>
{
    [GpuFact]
    public void StaticBentCasterGoldenMatchesCommittedGrid()
    {
        SkinnedPointShadowScene.Shot shot = fixture.StaticRigidAndBentSkinned()[2];
        GoldenCompare.AssertOrUpdate("scene3d_skinned_point_shadow_static", shot.Rgba,
            SkinnedPointShadowScene.Width, SkinnedPointShadowScene.Height);
    }

    [GpuFact]
    public void StaticPixelsShowRigidAndBentSkinnedShadowsWhileRigidBaseStaysCached()
    {
        IReadOnlyList<SkinnedPointShadowScene.Shot> shots = fixture.StaticRigidAndBentSkinned();

        Assert.Equal(1, shots[0].Diagnostics.PointStaticRebuilds);
        Assert.Equal(0, shots[1].Diagnostics.PointStaticRebuilds);
        Assert.Equal(0, shots[2].Diagnostics.PointStaticRebuilds);
        Assert.True(shots[1].Diagnostics.PointTransientRowsRendered > 0);
        Assert.True(shots[2].Diagnostics.PointStaticTransientSkinnedDrawCalls > 0);
        Assert.True(shots[2].RigidShadowLuminance < shots[2].LitLuminance * 0.8f);
        Assert.True(shots[2].SkinnedShadowLuminance < shots[2].SkinnedLitLuminance * 0.8f,
            $"skinned probe {shots[2].SkinnedShadowLuminance}, lit {shots[2].SkinnedLitLuminance}, "
            + $"base row {shots[2].BaseRow}, transient row {shots[2].TransientRow}");
    }

    [GpuTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void DynamicLightDrawsRigidAndBentSkinnedCastersIntoBaseOnly(bool gpuSkinning)
    {
        SkinnedPointShadowScene.Shot shot = fixture.DynamicRigidAndSkinned(gpuSkinning);

        Assert.Equal(-1, shot.TransientRow);
        Assert.Equal(0, shot.Diagnostics.PointTransientRowsRendered);
        Assert.True(shot.Diagnostics.PointDynamicSkinnedDrawCalls > 0);
        Assert.True(shot.RigidShadowLuminance < shot.LitLuminance * 0.8f);
        Assert.True(shot.SkinnedShadowLuminance < shot.SkinnedLitLuminance * 0.8f,
            $"skinned probe {shot.SkinnedShadowLuminance}, lit {shot.SkinnedLitLuminance}, "
            + $"base row {shot.BaseRow}, transient row {shot.TransientRow}");
    }

    [GpuTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void BaseRowOneAndTransientRowZeroLandUnderTheSecondLight(bool gpuSkinning)
    {
        SkinnedPointShadowScene.Shot shot = fixture.BaseOneTransientZero(gpuSkinning);

        Assert.Equal(1, shot.BaseRow);
        Assert.Equal(0, shot.TransientRow);
        Assert.True(shot.RigidShadowLuminance < shot.LitLuminance * 0.8f,
            $"first rigid probe {shot.RigidShadowLuminance}, lit {shot.LitLuminance}");
        Assert.True(shot.SkinnedShadowLuminance < shot.SkinnedLitLuminance * 0.8f,
            $"second skinned probe {shot.SkinnedShadowLuminance}, lit {shot.SkinnedLitLuminance}");
    }

    [GpuTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void HalfDissolveThinsTheSkinnedShadowAndOptOutKeepsTheBodyVisible(bool gpuSkinning)
    {
        (SkinnedPointShadowScene.Shot solid, SkinnedPointShadowScene.Shot half,
            SkinnedPointShadowScene.Shot optedOut) = fixture.PolicyVariants(gpuSkinning);
        long solidDarkening = SkinnedPointShadowScene.ShadowDarkening(optedOut, solid);
        long halfDarkening = SkinnedPointShadowScene.ShadowDarkening(optedOut, half);

        Assert.True(solid.Diagnostics.PointStaticTransientSkinnedDrawCalls > 0);
        Assert.True(half.Diagnostics.PointStaticTransientSkinnedDrawCalls > 0);
        Assert.Equal(0, optedOut.Diagnostics.PointStaticTransientSkinnedDrawCalls);
        Assert.True(optedOut.DrawnSkinnedInstances > 0);
        Assert.True(optedOut.BodyLuminance > 20,
            $"opted-out body probe reads {optedOut.BodyLuminance}");
        Assert.True(solidDarkening > 0);
        Assert.InRange((float)halfDarkening / solidDarkening, 0.2f, 0.9f);
    }

    [GpuTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void OffCameraSkinnedCasterShadowsAnOnCameraReceiverWithKeyLightOff(bool gpuSkinning)
    {
        (SkinnedPointShadowScene.Shot shadowed, SkinnedPointShadowScene.Shot unshadowed) =
            fixture.OffCameraSkinnedCaster(gpuSkinning);

        Assert.False(fixture.OnScreen(new System.Numerics.Vector3(0f, 1.1f, 16f)));
        Assert.True(shadowed.Diagnostics.PointStaticTransientSkinnedDrawCalls > 0);
        Assert.True(shadowed.SkinnedShadowLuminance <= unshadowed.SkinnedShadowLuminance - 20,
            $"receiver {shadowed.SkinnedShadowLuminance} shadowed against "
            + $"{unshadowed.SkinnedShadowLuminance} without the off-camera caster");
        Assert.True(System.Math.Abs(shadowed.SkinnedLitLuminance - unshadowed.SkinnedLitLuminance) <= 6);
    }

    [GpuTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void NearRadiusDiscardsAWholeSkinnedCasterButKeepsPartialFragments(bool gpuSkinning)
    {
        (SkinnedPointShadowScene.Shot solid, SkinnedPointShadowScene.Shot partial,
            SkinnedPointShadowScene.Shot excluded) = fixture.ClearanceVariants(gpuSkinning, exclusionBox: false);

        Assert.True(solid.Diagnostics.PointDynamicSkinnedDrawCalls > 0);
        Assert.True(partial.Diagnostics.PointDynamicSkinnedDrawCalls > 0);
        Assert.Equal(0, excluded.Diagnostics.PointDynamicSkinnedDrawCalls);
        Assert.True(fixture.FloorShadowDarkening(excluded, partial) > 0);
    }

    [GpuTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExclusionBoxDiscardsAWholeSkinnedCasterButKeepsPartialFragments(bool gpuSkinning)
    {
        (SkinnedPointShadowScene.Shot solid, SkinnedPointShadowScene.Shot partial,
            SkinnedPointShadowScene.Shot excluded) = fixture.ClearanceVariants(gpuSkinning, exclusionBox: true);

        Assert.True(solid.Diagnostics.PointDynamicSkinnedDrawCalls > 0);
        Assert.True(partial.Diagnostics.PointDynamicSkinnedDrawCalls > 0);
        Assert.Equal(0, excluded.Diagnostics.PointDynamicSkinnedDrawCalls);
        Assert.True(fixture.FloorShadowDarkening(excluded, partial) > 0);
    }

    [GpuTheory]
    [InlineData(true, false, 0f)]
    [InlineData(false, false, 0f)]
    [InlineData(true, true, 0f)]
    [InlineData(false, true, 0f)]
    [InlineData(true, false, 0.5f)]
    [InlineData(false, false, 0.5f)]
    public void LargeOriginKeepsStaticDynamicAndDissolvedShadowCoverage(
        bool gpuSkinning, bool dynamicLight, float dissolve)
    {
        (SkinnedPointShadowScene.Shot origin, SkinnedPointShadowScene.Shot far) =
            fixture.OriginPair(gpuSkinning, dynamicLight, dissolve);
        float[] nearGrid = GoldenCompare.Downsample(origin.Rgba,
            SkinnedPointShadowScene.Width, SkinnedPointShadowScene.Height);
        float[] farGrid = GoldenCompare.Downsample(far.Rgba,
            SkinnedPointShadowScene.Width, SkinnedPointShadowScene.Height);
        float worst = 0f;
        float brightest = 0f;
        for (int i = 0; i < nearGrid.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(nearGrid[i] - farGrid[i]));
            brightest = Math.Max(brightest, nearGrid[i]);
        }

        Assert.True(brightest > 0.3f, "the comparison scene must contain a lit receiver");
        Assert.True(dynamicLight
            ? origin.Diagnostics.PointDynamicSkinnedDrawCalls > 0
            : origin.Diagnostics.PointStaticTransientSkinnedDrawCalls > 0);
        Assert.True(worst <= GoldenCompare.InSessionTolerance,
            $"near and far coverage differ by {worst}, tolerance {GoldenCompare.InSessionTolerance}");
    }

    [GpuFact]
    public void AgedSharedSceneMatchesAFreshSceneAfterPointShadowConfigurationChanges()
    {
        (SkinnedPointShadowScene.Shot aged, SkinnedPointShadowScene.Shot fresh) =
            fixture.ReusedSceneMatchesFreshAfterConfigurationChanges();

        Assert.Equal(3, aged.Resolved.TransientAtlasRows);
        Assert.Equal(fresh.Resolved, aged.Resolved);
        Assert.Equivalent(fresh.Diagnostics, aged.Diagnostics, strict: true);
        Assert.Equal(fresh.Rgba, aged.Rgba);
    }
}
