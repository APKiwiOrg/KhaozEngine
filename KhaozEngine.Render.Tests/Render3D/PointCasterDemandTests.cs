using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class PointCasterDemandTests
{
    [Fact]
    public void MovingMembershipFollowsEachRequestAfterLightReorderingAndActorMovement()
    {
        using var rig = new RigidPointShadowRig();
        rig.Scene.FrustumCulling = false;
        ShadowPassDiagnostics Frame(float actorX, bool reverseLights = false, bool moving = true)
        {
            rig.Scene.Begin();
            rig.Scene.Draw(rig.Box, Matrix4x4.CreateTranslation(-2f, 0f, 0f));
            rig.Scene.Draw(rig.Box, Matrix4x4.CreateTranslation(62f, 0f, 0f));
            if (moving)
                rig.Scene.Draw(new RigidInstanceDraw(rig.Box, Matrix4x4.CreateTranslation(actorX, 0f, 0f))
                { ShadowMobility = ShadowCasterMobility.Dynamic });
            for (int i = 0; i < 2; i++)
            {
                int light = reverseLights ? 1 - i : i;
                rig.Scene.AddLight(new Vector3(light * 64f, 1f, 1f), Color.White, 3f, 1f,
                    LightShadow.Static(7 + light));
            }
            return rig.Render();
        }
        Frame(0f);
        Frame(0f);
        Frame(0f);
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 0;

        ShadowPassDiagnostics moved = Frame(64f, reverseLights: true);

        Assert.Equal(0, moved.PointStaticRebuilds);
        Assert.Equal(0, moved.PointRigidTransitionRowsRendered);
        Assert.Equal(1, moved.PointTransientDemand);
        Assert.Equal(1, moved.PointTransientRowsRendered);
        Assert.Equal(0, rig.Scene.PointShadowTransientSlotForLight(0));
        Assert.Equal(-1, rig.Scene.PointShadowTransientSlotForLight(1));
        Assert.Equal(6, moved.PointStaticTransientRigidDrawCalls);
        Assert.All(rig.RigidPointDraws(), draw => Assert.Equal(2u, draw.Slot));
        // Two signatures and one transient draw need at most three tests each, even under the full-walk shortcut.
        Assert.InRange(rig.Scene.PointCasterTouchTests, 0, 9);

        ShadowPassDiagnostics gone = Frame(64f, moving: false);

        Assert.Equal(0, gone.PointTransientDemand);
        Assert.Equal(0, gone.PointTransientRowsRendered);
        Assert.Equal(0, gone.PointFaceDrawCalls);
        Assert.Equal(-1, rig.Scene.PointShadowTransientSlotForLight(0));
        Assert.Equal(-1, rig.Scene.PointShadowTransientSlotForLight(1));
        // Only two signatures remain, each with at most two scenery casters to test.
        Assert.InRange(rig.Scene.PointCasterTouchTests, 0, 4);
    }

    [Fact]
    public void DynamicRequestsQueryRigidMembershipOnlyForSelectedDraws()
    {
        using var rig = new RigidPointShadowRig();
        rig.Settings.PointShadows.MaxDynamicLightsPerFrame = 1;
        rig.Frame(dynamicLights: true);

        ShadowPassDiagnostics current = rig.Frame(0.5f, dynamicLights: true);

        Assert.Equal(1, current.PointDynamicRenders);
        Assert.Equal(0, current.PointTransientDemand);
        Assert.Equal(0, current.PointTransientRowsRendered);
        Assert.Equal(6, current.PointFaceDrawCalls);
        // Only the selected draw queries, with at most the two rigid casters to test.
        Assert.InRange(rig.Scene.PointCasterTouchTests, 0, 2);
    }

    [Fact]
    public void ReplacementRequestsWithoutAdmissionCannotReusePreviousMovingDemand()
    {
        using var rig = new RigidPointShadowRig();
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 8;
        rig.Frame(lights: 8);
        rig.Frame(lights: 8);
        rig.Frame(lights: 8);
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 0;

        ShadowPassDiagnostics deferred = rig.Frame(lights: 8, moving: false, firstKey: 100);

        Assert.Equal(0, deferred.PointShadowedLights);
        Assert.Equal(0, deferred.PointTransientDemand);
        Assert.Equal(0, deferred.PointFaceDrawCalls);
        // The eight new signatures can each test the single remaining scenery caster once.
        Assert.InRange(rig.Scene.PointCasterTouchTests, 0, 8);

        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 8;
        ShadowPassDiagnostics admitted = rig.Frame(lights: 8, moving: false, firstKey: 100);

        Assert.Equal(8, admitted.PointStaticRebuilds);
        Assert.Equal(0, admitted.PointTransientDemand);
        Assert.Equal(0, admitted.PointRigidFallbackRowsRendered);
        Assert.Equal(0, admitted.PointRigidTransitionRowsRendered);
        Assert.Equal(0, admitted.PointTransientRowsRendered);
        Assert.Equal(30, admitted.PointFaceDrawCalls);
        Assert.All(Enumerable.Range(0, 8), light =>
            Assert.Equal(-1, rig.Scene.PointShadowTransientSlotForLight(light)));
    }
}
