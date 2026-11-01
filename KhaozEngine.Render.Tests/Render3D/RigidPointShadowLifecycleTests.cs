using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class RigidPointShadowLifecycleTests
{
    [Fact]
    public void RecoveryClearsFallbackBasesBeforePublishingSplitDepth()
    {
        using var rig = new RigidPointShadowRig();
        rig.Frame();
        ShadowPassDiagnostics fallback = rig.Frame(0.1f);
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 0;

        ShadowPassDiagnostics recovered = rig.Frame(0.5f);

        Assert.Equal(2, fallback.PointRigidFallbackRowsRendered);
        Assert.Equal(0, fallback.PointStaticRebuilds);
        Assert.Equal(2, recovered.PointRigidTransitionRowsRendered);
        Assert.Equal(0, recovered.PointStaticRebuilds);
        Assert.Equal(0, recovered.PointRigidFallbackRowsRendered);
        Assert.Equal(12, recovered.PointStaticTransientRigidDrawCalls);
        Assert.Equal(24, recovered.PointFaceDrawCalls);
        Assert.Equal(24, rig.RigidPointDraws().Length);
        Assert.Equal(0.5f, BitConverter.ToSingle(Assert.IsType<byte[]>(rig.InstanceUpload), 176));
    }

    [Fact]
    public void DemandGrowthUsesOldCapacityAndFallbackUntilNextBoundary()
    {
        using var rig = new RigidPointShadowRig();
        rig.Frame(lights: 1);
        rig.Frame(lights: 1);
        rig.Frame(lights: 1);

        ShadowPassDiagnostics growing = rig.Frame(0.3f);
        ShadowPassDiagnostics grown = rig.Frame(0.5f);

        Assert.Equal(2, growing.PointTransientDemand);
        Assert.Equal(1, growing.PointTransientRowsRendered);
        Assert.Equal(1, growing.PointRigidFallbackRowsRendered);
        Assert.Equal(2, grown.PointTransientRowsRendered);
        Assert.Equal(0, grown.PointRigidFallbackRowsRendered);
        Assert.Equal(1, grown.PointRigidTransitionRowsRendered);
    }

    [Fact]
    public void SimultaneousSceneryChangeGetsOneMandatoryPureBaseRenderPerLight()
    {
        using var rig = new RigidPointShadowRig();
        rig.Warm();
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 0;

        ShadowPassDiagnostics current = rig.Frame(0.5f, sceneryX: -3f);

        Assert.Equal(0, current.PointStaticRebuilds);
        Assert.Equal(2, current.PointRigidTransitionRowsRendered);
        Assert.Equal(24, current.PointFaceDrawCalls);
    }

    [Fact]
    public void EvictedFallbackOwnerCannotForceWorkForUnrenderedReplacement()
    {
        using var rig = new RigidPointShadowRig();
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 8;
        rig.Frame(lights: 8);
        rig.Frame(lights: 8);
        rig.Factory.ThrowOnTextureCreate = rig.Factory.Textures.Count + 1;
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 0;

        ShadowPassDiagnostics deferred = rig.Frame(lights: 8, moving: false, firstKey: 100);
        Assert.Equal(0, deferred.PointShadowedLights);
        Assert.Equal(0, deferred.PointFaceDrawCalls);

        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 8;
        ShadowPassDiagnostics admitted = rig.Frame(lights: 8, moving: false, firstKey: 100);
        Assert.Equal(8, admitted.PointStaticRebuilds);
        Assert.Equal(0, admitted.PointRigidFallbackRowsRendered);
        Assert.Equal(0, admitted.PointRigidTransitionRowsRendered);
    }

    [Fact]
    public void DissolveMeshReplacementAndOriginRebaseStayOutsideTheStaticSignature()
    {
        using var rig = new RigidPointShadowRig();
        rig.Warm();
        MeshHandle replacement = rig.Scene.LoadMesh(MeshPrimitives.Box(0.75f));
        Vector3 origin = new(100_000f, 0f, -100_000f);
        rig.Scene.RenderOrigin = origin;
        rig.Scene.Camera.Target = origin;
        rig.Scene.FrustumCulling = false;
        // Keep absolute scene geometry fixed while changing only render origin for the first proof.
        ShadowPassDiagnostics rebased = rig.Frame(0.5f);
        Assert.Equal(0, rebased.PointStaticRebuilds);
        Assert.Equal(12, rebased.PointStaticTransientRigidDrawCalls);
        Assert.Equal(-99_999.5f, BitConverter.ToSingle(Assert.IsType<byte[]>(rig.InstanceUpload), 176));

        ShadowPassDiagnostics replaced = rig.Frame(0.75f, dissolve: 0.5f, actorMesh: replacement);
        Assert.Equal(0, replaced.PointStaticRebuilds);
        Assert.Equal(0, replaced.PointRigidTransitionRowsRendered);
        Assert.Equal(12, replaced.PointStaticTransientRigidDrawCalls);
        Assert.Equal(12, replaced.PointFaceDrawCalls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GrowthRefusalKeepsOldRowsAndRedrawsUnassignedLights(bool refuseBind)
    {
        using var rig = new RigidPointShadowRig();
        rig.Frame(lights: 1);
        rig.Frame(lights: 1);
        rig.Frame(lights: 1);
        rig.Frame();
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 0;
        if (refuseBind) rig.Factory.ThrowOnResourceSetCreate = rig.Factory.ResourceSets.Count + 1;
        else rig.Factory.ThrowOnTextureCreate = rig.Factory.Textures.Count + 1;

        ShadowPassDiagnostics current = rig.Frame(0.5f);

        Assert.Equal(1, rig.Scene.PointShadowTransientRows);
        Assert.Equal(2, current.PointShadowedLights);
        Assert.Equal(1, current.PointTransientRowsRendered);
        Assert.Equal(1, current.PointRigidFallbackRowsRendered);
        Assert.Equal(12, current.PointFaceDrawCalls);
        Assert.Equal(0, current.PointStaticRebuilds);
        Assert.Equal(0.5f, BitConverter.ToSingle(Assert.IsType<byte[]>(rig.InstanceUpload), 176));
    }

    [Fact]
    public void AtlasReplacementResetsFallbackContentBeforeNewAdmission()
    {
        using var rig = new RigidPointShadowRig();
        rig.Frame();
        rig.Frame();
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 0;
        rig.Scene.RequestPointShadowSettings(new PointShadowSettings
        { FaceResolution = 128, MaxStaticRebuildsPerFrame = 0 });

        ShadowPassDiagnostics deferred = rig.Frame(moving: false);
        Assert.Equal(0, deferred.PointShadowedLights);
        Assert.Equal(0, deferred.PointRigidTransitionRowsRendered);
        Assert.Equal(0, deferred.PointFaceDrawCalls);

        rig.Scene.RequestPointShadowSettings(new PointShadowSettings
        { FaceResolution = 128, MaxStaticRebuildsPerFrame = 2 });
        ShadowPassDiagnostics admitted = rig.Frame(moving: false);
        Assert.Equal(2, admitted.PointStaticRebuilds);
        Assert.Equal(0, admitted.PointRigidTransitionRowsRendered);
        Assert.Equal(0, admitted.PointRigidFallbackRowsRendered);
        Assert.Equal(12, admitted.PointFaceDrawCalls);
    }

}
