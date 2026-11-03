using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class RigidPointShadowSchedulingTests
{
    [Fact]
    public void MovingRigidActorKeepsTwoWarmedStaticRowsCached()
    {
        using var rig = new RigidPointShadowRig();
        rig.Warm();

        ShadowPassDiagnostics current = rig.Frame(0.5f);

        Assert.Equal(0, current.PointStaticRebuilds);
        Assert.Equal(2, current.PointTransientRowsRendered);
        Assert.Equal(0, rig.Scene.PointShadowBaseSlotForLight(0));
        Assert.Equal(1, rig.Scene.PointShadowBaseSlotForLight(1));
        Assert.Equal(1, rig.Scene.PointShadowTransientSlotForLight(0));
        Assert.Equal(0, rig.Scene.PointShadowTransientSlotForLight(1));
        Assert.Equal(12, current.PointFaceDrawCalls);
        Assert.Equal(12, rig.RigidPointDraws().Length);
        Assert.Equal(0.5f, BitConverter.ToSingle(Assert.IsType<byte[]>(rig.InstanceUpload), 176));
        Assert.All(rig.RigidPointDraws(), draw => { Assert.Equal(1u, draw.Slot); Assert.Equal(1u, draw.Value); });
    }

    [Fact]
    public void StaticDefaultMotionStillInvalidatesBothBaseRows()
    {
        using var rig = new RigidPointShadowRig();
        rig.Frame(mobility: ShadowCasterMobility.Static, motion: MotionKey.From(9));
        rig.Frame(mobility: ShadowCasterMobility.Static, motion: MotionKey.From(9));

        ShadowPassDiagnostics current = rig.Frame(0.5f, mobility: ShadowCasterMobility.Static, motion: MotionKey.From(9));

        Assert.Equal(2, current.PointStaticRebuilds);
        Assert.Equal(0, current.PointTransientRowsRendered);
        Assert.Equal(12, current.PointFaceDrawCalls);
    }

    [Fact]
    public void DynamicLightsRenderAllRigidCastersWithoutTransientRows()
    {
        using var rig = new RigidPointShadowRig();
        rig.Frame(dynamicLights: true);
        ShadowPassDiagnostics current = rig.Frame(0.5f, dynamicLights: true);

        Assert.Equal(2, current.PointDynamicRenders);
        Assert.Equal(0, current.PointTransientRowsRendered);
        Assert.Equal(12, current.PointFaceDrawCalls);
    }

    [Fact]
    public void UnsupportedMobilityIsRejectedBeforeQueueMutation()
    {
        using var rig = new RigidPointShadowRig();
        rig.Scene.Begin();

        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Scene.Draw(new RigidInstanceDraw
        {
            ShadowMobility = (ShadowCasterMobility)2,
        }));

        Assert.Empty(rig.Scene.QueuedInstancesForTests);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MixedSkinnedAndMovingRigidShareCurrentTransientRows(bool gpuSkinning)
    {
        using var rig = new RigidPointShadowRig();
        rig.Scene.UseGpuSkinning = gpuSkinning;
        rig.Warm();

        ShadowPassDiagnostics current = rig.Frame(0.5f, skinned: true);

        Assert.Equal(0, current.PointStaticRebuilds);
        Assert.Equal(2, current.PointTransientRowsRendered);
        Assert.Equal(12, current.PointStaticTransientSkinnedDrawCalls);
        Assert.Equal(24, current.PointFaceDrawCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShadowOnlyAndOffCameraRigidCastersKeepCurrentPointContribution(bool shadowOnly)
    {
        using var rig = new RigidPointShadowRig();
        rig.Warm();
        if (!shadowOnly) rig.Scene.Camera.Target = new System.Numerics.Vector3(100f, 0f, 0f);
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 0;

        ShadowPassDiagnostics current = rig.Frame(0.5f, shadowOnly: shadowOnly);

        Assert.Equal(2, current.PointTransientRowsRendered);
        Assert.Equal(12, current.PointFaceDrawCalls);
        Assert.Equal(0, current.PointStaticRebuilds);
    }

    [Fact]
    public void CastOptOutAndDisappearanceStopPublishingTransientDepth()
    {
        using var rig = new RigidPointShadowRig();
        rig.Warm();

        ShadowPassDiagnostics optedOut = rig.Frame(casts: false);
        Assert.Equal(0, optedOut.PointTransientRowsRendered);
        Assert.Equal(0, optedOut.PointFaceDrawCalls);
        Assert.Equal(-1, rig.Scene.PointShadowTransientSlotForLight(0));

        ShadowPassDiagnostics gone = rig.Frame(moving: false);
        Assert.Equal(0, gone.PointFaceDrawCalls);
        Assert.Equal(-1, rig.Scene.PointShadowTransientSlotForLight(1));
    }

    [Fact]
    public void SameMeshInterleavingSubmitsOnlyTheTwoCurrentMovingSlots()
    {
        using var rig = new RigidPointShadowRig();
        ShadowPassDiagnostics Frame(float x)
        {
            rig.Scene.Begin();
            rig.Scene.Draw(rig.Box, Matrix4x4.CreateTranslation(-2f, 0f, 0f));
            rig.Scene.Draw(new RigidInstanceDraw(rig.Box, Matrix4x4.CreateTranslation(x, 0f, 0f))
            { ShadowMobility = ShadowCasterMobility.Dynamic });
            rig.Scene.Draw(rig.Box, Matrix4x4.CreateTranslation(2f, 0f, 0f));
            rig.Scene.Draw(new RigidInstanceDraw(rig.Box, Matrix4x4.CreateTranslation(x, 0f, 2f))
            { ShadowMobility = ShadowCasterMobility.Dynamic });
            rig.Scene.AddLight(new Vector3(0f, 2f, 0f), Color.White, 10f, 1f, LightShadow.Static(7));
            return rig.Render();
        }
        Frame(0f);
        Frame(0f);
        Frame(0f);

        ShadowPassDiagnostics current = Frame(0.5f);

        Assert.Equal(0, current.PointStaticRebuilds);
        Assert.Equal(12, current.PointStaticTransientRigidDrawCalls);
        Assert.Equal(new uint[] { 1, 3, 1, 3, 1, 3, 1, 3, 1, 3, 1, 3 },
            rig.RigidPointDraws().Select(draw => draw.Slot));
        Assert.All(rig.RigidPointDraws(), draw => Assert.Equal(1u, draw.Value));
    }

}
