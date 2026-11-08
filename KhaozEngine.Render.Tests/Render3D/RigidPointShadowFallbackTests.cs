using System;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class RigidPointShadowFallbackTests
{
    [Fact]
    public void FirstRigidDemandRendersCompleteBaseBeforeTransientAllocation()
    {
        using var rig = new RigidPointShadowRig();
        rig.Frame();
        ShadowPassDiagnostics current = rig.Frame();

        Assert.Equal(2, current.PointShadowedLights);
        Assert.Equal(0, current.PointTransientRowsRendered);
        Assert.Equal(12, current.PointFaceDrawCalls);
        Assert.Equal(-1, rig.Scene.PointShadowTransientSlotForLight(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusedTransientStorageRedrawsCompleteBaseAtZeroOrdinaryBudget(bool refuseBind)
    {
        using var rig = new RigidPointShadowRig();
        rig.Frame(skinned: true);
        rig.Frame(skinned: true);
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 0;
        if (refuseBind) rig.Factory.ThrowOnResourceSetCreate = rig.Factory.ResourceSets.Count + 1;
        else rig.Factory.ThrowOnTextureCreate = rig.Factory.Textures.Count + 1;

        ShadowPassDiagnostics current = rig.Frame(0.5f, skinned: true);

        Assert.Equal(2, current.PointShadowedLights);
        Assert.Equal(0, current.PointTransientRowsRendered);
        Assert.Equal(2, current.PointRigidFallbackRowsRendered);
        Assert.Equal(12, current.PointStaticFallbackSkinnedDrawCalls);
        Assert.Equal(0, current.PointStaticTransientSkinnedDrawCalls);
        Assert.Equal(24, current.PointFaceDrawCalls);
        Assert.Equal(0, current.PointStaticRebuilds);
        Assert.Equal(-1, rig.Scene.PointShadowTransientSlotForLight(0));
    }

    [Fact]
    public void FallbackDisappearanceClearsGhostsOutsideOrdinaryBudget()
    {
        using var rig = new RigidPointShadowRig();
        rig.Frame();
        rig.Frame();
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 0;
        rig.Factory.ThrowOnTextureCreate = rig.Factory.Textures.Count + 1;

        ShadowPassDiagnostics current = rig.Frame(moving: false);

        Assert.Equal(2, current.PointShadowedLights);
        Assert.Equal(12, current.PointFaceDrawCalls);
        Assert.Equal(2, current.PointRigidTransitionRowsRendered);
        Assert.Equal(0, current.PointTransientRowsRendered);
        Assert.Equal(0, current.PointStaticRebuilds);
    }

    [Theory]
    [InlineData(ShadowCasterMobility.Static, ShadowCasterMobility.Dynamic)]
    [InlineData(ShadowCasterMobility.Dynamic, ShadowCasterMobility.Static)]
    public void MobilitySwitchPreservesCurrentShadowWithZeroOrdinaryBudget(
        ShadowCasterMobility before, ShadowCasterMobility after)
    {
        using var rig = new RigidPointShadowRig();
        rig.Frame(mobility: before);
        rig.Frame(mobility: before);
        rig.Frame(mobility: before);
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 0;

        ShadowPassDiagnostics current = rig.Frame(0.5f, mobility: after);

        Assert.Equal(2, current.PointShadowedLights);
        Assert.True(current.PointFaceDrawCalls >= 12);
        Assert.Equal(0, current.PointStaticRebuilds);
    }

    [Fact]
    public void StaticToMovingSwitchClearsCachedPoseBeforeLiveSplitSampling()
    {
        using var rig = new RigidPointShadowRig();
        rig.Warm();
        Assert.Equal(2, rig.Scene.PointShadowTransientRows);
        rig.Frame(mobility: ShadowCasterMobility.Static);
        Assert.All(rig.RigidPointDraws(), draw => { Assert.Equal(0u, draw.Slot); Assert.Equal(2u, draw.Value); });
        ShadowPassDiagnostics held = rig.Frame(mobility: ShadowCasterMobility.Static);
        Assert.Equal(2, held.PointShadowedLights);
        Assert.Equal(0, held.PointFaceDrawCalls);
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 0;

        ShadowPassDiagnostics current = rig.Frame(0.5f);

        Assert.Equal(2, current.PointRigidTransitionRowsRendered);
        Assert.Equal(0, current.PointRigidFallbackRowsRendered);
        Assert.Equal(0, current.PointStaticRebuilds);
        Assert.Equal(2, current.PointTransientRowsRendered);
        Assert.Equal(12, current.PointStaticTransientRigidDrawCalls);
        Assert.Equal(24, current.PointFaceDrawCalls);
        var draws = rig.RigidPointDraws();
        Assert.Equal(24, draws.Length);
        // Two lights with six faces each. Base rows contain scenery, transient rows contain the current actor.
        Assert.All(draws.Take(12), draw => { Assert.Equal(0u, draw.Slot); Assert.Equal(1u, draw.Value); });
        Assert.All(draws.Skip(12), draw => { Assert.Equal(1u, draw.Slot); Assert.Equal(1u, draw.Value); });
        Assert.Equal(0.5f, BitConverter.ToSingle(Assert.IsType<byte[]>(rig.InstanceUpload), 176));
    }

    [Fact]
    public void UnadmittedRigidRowHonorsZeroOrdinaryBudget()
    {
        using var rig = new RigidPointShadowRig();
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 0;
        rig.Frame();

        ShadowPassDiagnostics current = rig.Frame();

        Assert.Equal(0, current.PointShadowedLights);
        Assert.Equal(0, current.PointTransientDemand);
        Assert.Equal(0, current.PointRigidFallbackRowsRendered);
        Assert.Equal(0, current.PointFaceDrawCalls);
    }

    [Fact]
    public void SkinnedOnlyRemainderKeepsFallbackCurrentUntilStorageRecovers()
    {
        using var rig = new RigidPointShadowRig();
        rig.Frame(skinned: true);
        rig.Frame(skinned: true);
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 0;
        rig.Factory.ThrowOnTextureCreate = rig.Factory.Textures.Count + 1;

        ShadowPassDiagnostics current = rig.Frame(moving: false, skinned: true);

        Assert.Equal(2, current.PointRigidFallbackRowsRendered);
        Assert.Equal(12, current.PointStaticFallbackSkinnedDrawCalls);
        Assert.Equal(24, current.PointFaceDrawCalls);
        Assert.Equal(0, current.PointTransientRowsRendered);
    }

}
