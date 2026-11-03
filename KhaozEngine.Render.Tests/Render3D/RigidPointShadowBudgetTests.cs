using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class RigidPointShadowBudgetTests
{
    [Fact]
    public void RepeatedMandatoryTransitionsLeaveTheOrdinaryRefreshAvailable()
    {
        using var rig = new RigidPointShadowRig();
        WarmTogether(rig);
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 1;

        foreach (float offset in new[] { 0.5f, 0.75f, 1f })
        {
            ShadowPassDiagnostics current = Frame(rig, offset);

            Assert.Equal(1, current.PointRigidTransitionRowsRendered);
            Assert.Equal(1, current.PointStaticRebuilds);
            Assert.Equal(1, current.PointTransientRowsRendered);
            Assert.Equal(18, current.PointFaceDrawCalls);
            AssertSceneryRefreshed(rig, 1);
        }
    }

    [Fact]
    public void RefusedTransientStorageLeavesTheOrdinaryRefreshAvailableEveryFrame()
    {
        using var rig = new RigidPointShadowRig();
        Frame(rig, 0f);
        Frame(rig, 0f);
        rig.Factory.ThrowOnTextureCreate = rig.Factory.Textures.Count + 1;
        ShadowPassDiagnostics together = Frame(rig, 0.25f);
        Assert.Equal(1, together.PointRigidFallbackRowsRendered);
        Assert.Equal(1, together.PointStaticRebuilds);
        AssertSceneryRefreshed(rig, 0);
        AssertSceneryRefreshed(rig, 1);
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 1;

        foreach (float offset in new[] { 0.5f, 0.75f, 1f })
        {
            ShadowPassDiagnostics current = Frame(rig, offset);

            Assert.Equal(1, current.PointRigidFallbackRowsRendered);
            Assert.Equal(1, current.PointStaticRebuilds);
            Assert.Equal(0, current.PointTransientRowsRendered);
            Assert.Equal(18, current.PointFaceDrawCalls);
            AssertSceneryRefreshed(rig, 1);
        }
    }

    [Fact]
    public void MandatoryWorkAllowsOneNewFallbackAdmissionWhileTheNextRowWaits()
    {
        using var rig = new RigidPointShadowRig();
        Frame(rig, 0f, lights: 1);
        Frame(rig, 0f, lights: 1);
        Frame(rig, 0f, lights: 1);
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 1;

        ShadowPassDiagnostics current = Frame(rig, 0.25f, lights: 3, allMoving: true);

        Assert.Equal(2, current.PointShadowedLights);
        Assert.Equal(1, current.PointRigidTransitionRowsRendered);
        Assert.Equal(1, current.PointRigidFallbackRowsRendered);
        Assert.Equal(0, current.PointStaticRebuilds);
        Assert.Equal(2, current.PointTransientDemand);
        Assert.Equal(1, current.PointTransientRowsRendered);
        Assert.Equal(24, current.PointFaceDrawCalls);
        Assert.Equal(-1, rig.Scene.PointShadowTransientSlotForLight(1));
        Assert.Equal(-1, rig.Scene.PointShadowBaseSlotForLight(2));
        AssertSceneryRefreshed(rig, 1);
        AssertSceneryRefreshed(rig, 4);
        Assert.DoesNotContain(rig.RigidPointDraws(), draw => draw.Slot is 2 or 5);

        ShadowPassDiagnostics admitted = Frame(rig, 0.5f, lights: 3, allMoving: true);

        Assert.Equal(3, admitted.PointShadowedLights);
        Assert.Equal(2, admitted.PointRigidTransitionRowsRendered);
        Assert.Equal(1, admitted.PointRigidFallbackRowsRendered);
        Assert.Equal(0, admitted.PointStaticRebuilds);
        Assert.Equal(3, admitted.PointTransientDemand);
        AssertSceneryRefreshed(rig, 2);
        AssertSceneryRefreshed(rig, 5);
    }

    [Fact]
    public void ReturningToRenderedSignatureKeepsMandatorySelectionAndRenderingConsistent()
    {
        using var rig = new RigidPointShadowRig();
        WarmTogether(rig);
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 0;
        Frame(rig, 0.25f, moving: false);
        ShadowPassDiagnostics deferred = Frame(rig, 0.5f, moving: false);
        Assert.Equal(0, deferred.PointFaceDrawCalls);
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 1;

        ShadowPassDiagnostics current = Frame(rig, 0.25f);

        Assert.Equal(1, current.PointRigidTransitionRowsRendered);
        Assert.Equal(1, current.PointStaticRebuilds);
        Assert.Equal(18, current.PointFaceDrawCalls);
        AssertSceneryRefreshed(rig, 1);
    }

    static void WarmTogether(RigidPointShadowRig rig)
    {
        Frame(rig, 0f);
        Frame(rig, 0f);
        Frame(rig, 0f);
        ShadowPassDiagnostics together = Frame(rig, 0.25f);
        Assert.Equal(1, together.PointRigidTransitionRowsRendered);
        Assert.Equal(1, together.PointStaticRebuilds);
        AssertSceneryRefreshed(rig, 0);
        AssertSceneryRefreshed(rig, 1);
    }

    static ShadowPassDiagnostics Frame(RigidPointShadowRig rig, float offset, int lights = 2,
        bool moving = true, bool allMoving = false)
    {
        rig.Scene.Camera.Azimuth = 0f;
        rig.Scene.Begin();
        for (int i = 0; i < lights; i++)
            rig.Scene.Draw(rig.Box, Matrix4x4.CreateTranslation(i * 12f + offset, 0f, 0f));
        if (moving)
            for (int i = 0; i < (allMoving ? lights : 1); i++)
                rig.Scene.Draw(new RigidInstanceDraw(rig.Box,
                    Matrix4x4.CreateTranslation(i * 12f - 1f, 0f, 0f))
                { ShadowMobility = ShadowCasterMobility.Dynamic });
        for (int i = 0; i < lights; i++)
            rig.Scene.AddLight(new Vector3(i * 12f, 1f, 1f), Color.White, 3f, 1f,
                LightShadow.Static(7 + i));
        return rig.Render();
    }

    static void AssertSceneryRefreshed(RigidPointShadowRig rig, uint slot)
    {
        var draws = rig.RigidPointDraws().Where(draw => draw.Slot == slot).ToArray();
        Assert.Equal(6, draws.Length);
        Assert.All(draws, draw => Assert.Equal(1u, draw.Value));
    }
}
