using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class RigidPointShadowGpuTests(RigidPointShadowScene fixture, ITestOutputHelper output)
    : IClassFixture<RigidPointShadowScene>
{
    bool _capturedFailure;
    [GpuTheory]
    [InlineData(PointShadowFilter.Hard)]
    [InlineData(PointShadowFilter.Soft)]
    public void MovingMixedCastersMatchFullRedrawThroughFallbackGrowthAndMobilityChanges(PointShadowFilter filter)
    {
        Warm(filter);
        var pose = new RigidPointShadowScene.Pose();
        RigidPointShadowScene.Pair first = Check("first-demand", fixture.Capture(pose));
        Assert.Equal(1, first.Split.Work.PointRigidFallbackRowsRendered);
        Assert.Equal(6, first.Split.Work.PointStaticFallbackSkinnedDrawCalls);
        Assert.Equal(0, first.Split.Work.PointStaticRebuilds);
        Assert.Equal(-1, first.Split.TransientRows[1]);

        RigidPointShadowScene.Pair recovered = Check("first-recovery", fixture.Capture(pose));
        Assert.Equal(1, recovered.Split.Work.PointRigidTransitionRowsRendered);
        Assert.Equal(1, recovered.Split.Resolution.TransientAtlasRows);
        Assert.Equal(0, recovered.Split.BaseRows[1]);
        Assert.Equal(0, recovered.Split.TransientRows[1]);

        pose = pose with { Z = 2f };
        RigidPointShadowScene.Pair moving = Check("cached-moving", fixture.Capture(pose));
        Cached(moving.Split);
        Assert.True(moving.Split.Work.PointStaticTransientRigidDrawCalls > 0);
        Assert.True(moving.Split.Work.PointStaticTransientSkinnedDrawCalls > 0);
        Assert.Equal(1, moving.Split.Work.PointTransientRowsRendered);
        Assert.Equal(moving.Split.Work.PointStaticTransientRigidDrawCalls
            + moving.Split.Work.PointStaticTransientSkinnedDrawCalls, moving.Split.Work.PointFaceDrawCalls);
        Assert.NotEqual(recovered.Split.Rgba, moving.Split.Rgba);
        ShadowControls(pose, moving.Split);

        pose = pose with { Z = -2f };
        RigidPointShadowScene.Pair growth = Check("growth-fallback", fixture.Capture(pose));
        Assert.Equal(2, growth.Split.Work.PointTransientDemand);
        Assert.Equal(1, growth.Split.Work.PointRigidFallbackRowsRendered);
        Assert.Equal(0, growth.Split.Work.PointStaticRebuilds);
        RigidPointShadowScene.Pair grown = Check("growth-recovery", fixture.Capture(pose));
        Assert.Equal(1, grown.Split.Work.PointRigidTransitionRowsRendered);
        Assert.Equal(2, grown.Split.Work.PointTransientRowsRendered);
        Assert.Equal(2, grown.Split.Resolution.TransientAtlasRows);
        Assert.Equal(7, grown.Split.Resolution.MaxShadowedLights);
        Assert.Equal(2, grown.Split.BaseRows[2]);
        Assert.Equal(1, grown.Split.TransientRows[2]);

        pose = pose with { Mobility = ShadowCasterMobility.Static };
        RigidPointShadowScene.Pair stationary = Check("dynamic-to-static", fixture.Capture(pose));
        Assert.Equal(2, stationary.Split.Work.PointRigidTransitionRowsRendered);
        Assert.Equal(0, stationary.Split.Work.PointStaticTransientRigidDrawCalls);
        Assert.Equal(0, stationary.Split.Work.PointStaticRebuilds);

        pose = pose with { Mobility = ShadowCasterMobility.Dynamic, Z = -1.75f };
        RigidPointShadowScene.Pair dynamic = Check("static-to-dynamic", fixture.Capture(pose));
        Assert.Equal(2, dynamic.Split.Work.PointRigidTransitionRowsRendered);
        Assert.True(dynamic.Split.Work.PointStaticTransientRigidDrawCalls > 0);
        pose = pose with { Actor = false };
        RigidPointShadowScene.Pair absent = Check("disappeared", fixture.Capture(pose));
        Cached(absent.Split);
        Assert.Equal(0, absent.Split.Work.PointStaticTransientRigidDrawCalls);
        Assert.Equal(-1, absent.Split.TransientRows[2]);
        ShadowControls(pose, absent.Split);
    }

    [GpuFact]
    public void DisappearanceClearsCombinedFallbackDepthBeforeTransientSampling()
    {
        Warm(PointShadowFilter.Hard);
        var actor = new RigidPointShadowScene.Pose();
        RigidPointShadowScene.Pair fallback = Check("vanish-fallback", fixture.Capture(actor));
        Assert.Equal(1, fallback.Split.Work.PointRigidFallbackRowsRendered);
        RigidPointShadowScene.Pair absent = Check("vanish-recovery", fixture.Capture(actor with { Actor = false }));
        Assert.Equal(1, absent.Split.Work.PointRigidTransitionRowsRendered);
        Assert.Equal(0, absent.Split.Work.PointStaticTransientRigidDrawCalls);
        Assert.Equal(0, absent.Split.Work.PointStaticRebuilds);
    }

    [GpuFact]
    public void AgedSharedSceneMatchesFreshAfterLayerAndFilterChanges()
    {
        Warm(PointShadowFilter.Soft);
        fixture.Capture(new RigidPointShadowScene.Pose());
        fixture.Capture(new RigidPointShadowScene.Pose(Z: -2f));
        fixture.Capture(new RigidPointShadowScene.Pose(Mobility: ShadowCasterMobility.Static));
        RigidPointShadowScene.Pair aged = Stable(fixture);
        using var fresh = new RigidPointShadowScene();
        RigidPointShadowScene.Pair clean = Stable(fresh);
        Assert.Equal(clean.Split.Rgba, aged.Split.Rgba);
        Assert.Equivalent(clean.Split.Work, aged.Split.Work, strict: true);
        Assert.Equal(clean.Split.Resolution, aged.Split.Resolution);
        Check("aged-parity", aged);
    }

    void Warm(PointShadowFilter filter, [CallerMemberName] string testCase = "")
    {
        fixture.Reset(filter);
        var scenery = new RigidPointShadowScene.Pose(Actor: false, Skinned: false);
        fixture.Capture(scenery);
        RigidPointShadowScene.Pair warmed = Check("static-warm", fixture.Capture(scenery), testCase);
        Assert.Equal(3, warmed.Split.Work.PointStaticRebuilds);
        Assert.Equal(0, warmed.Split.Resolution.TransientAtlasRows);
        fixture.FreezeOrdinaryRefresh();
    }

    static RigidPointShadowScene.Pair Stable(RigidPointShadowScene scene)
    {
        scene.Reset(PointShadowFilter.Hard);
        var pose = new RigidPointShadowScene.Pose(Z: -2f);
        scene.Capture(pose);
        scene.Capture(pose);
        return scene.Capture(pose);
    }

    static void Cached(RigidPointShadowScene.Shot shot)
    {
        Assert.Equal(0, shot.Work.PointStaticRebuilds);
        Assert.Equal(0, shot.Work.PointRigidFallbackRowsRendered);
        Assert.Equal(0, shot.Work.PointRigidTransitionRowsRendered);
    }

    void ShadowControls(RigidPointShadowScene.Pose pose, RigidPointShadowScene.Shot shadowed)
    {
        // Casting alone changes in these controls. Bodies, camera, lighting and current transforms are identical.
        RigidPointShadowScene.Shot noScenery = fixture.FullControl(pose with { SceneryCasts = false });
        Probe("fixed scenery", shadowed, noScenery, new Vector3(-1f, 0f, 0f));
        Probe("fixed scenery beside growth light", shadowed, noScenery, new Vector3(8.3f, 0f, -7f));
        RigidPointShadowScene.Shot noSkin = fixture.FullControl(pose with { SkinnedCasts = false });
        Probe("skinned caster", shadowed, noSkin, new Vector3(7f, 0f, 4f));
        if (pose.Actor)
        {
            RigidPointShadowScene.Shot noActor = fixture.FullControl(pose with { ActorCasts = false });
            Probe("moving actor", shadowed, noActor, new Vector3(8.3f, 0f, pose.Z * 1.5f));
        }
        Assert.True(fixture.Red(shadowed, new Vector3(-8f, 0f, 0f)) > 50);
    }

    void Probe(string name, RigidPointShadowScene.Shot shadowed, RigidPointShadowScene.Shot control, Vector3 at)
    {
        int dark = fixture.Red(shadowed, at), lit = fixture.Red(control, at);
        output.WriteLine($"{name} receiver at {at}: shadow={dark}, omitted-caster={lit}");
        Assert.True(lit - dark >= 20, $"{name} receiver must darken by at least 20 bytes: {dark} vs {lit}");
    }

    RigidPointShadowScene.Pair Check(string name, RigidPointShadowScene.Pair pair, [CallerMemberName] string testCase = "")
    {
        Assert.Equal(3, pair.Full.Work.PointDynamicRenders);
        Assert.Equal(3, pair.Full.Work.PointShadowedLights);
        Assert.Equal(0, pair.Full.Work.PointTransientRowsRendered);
        Assert.All(pair.Full.TransientRows, row => Assert.Equal(-1, row));
        Assert.Equal(3, pair.Split.Work.PointShadowedLights);
        Assert.Equal(256, pair.Split.Resolution.FaceResolution);
        Assert.Equal(pair.Full.Resolution.FaceResolution, pair.Split.Resolution.FaceResolution);
        Assert.Equal(pair.Full.Resolution.MaxShadowedLights, pair.Split.Resolution.MaxShadowedLights);
        Assert.Equal(pair.Full.BaseRows, pair.Split.BaseRows);
        Assert.False(pair.Split.Resolution.Degraded);
        Assert.False(pair.Full.Resolution.Degraded);
        int changed = 0, worst = 0;
        for (int i = 0; i < pair.Full.Rgba.Length; i++)
        {
            int delta = Math.Abs(pair.Full.Rgba[i] - pair.Split.Rgba[i]);
            if (delta > 0) changed++;
            worst = Math.Max(worst, delta);
        }
        output.WriteLine($"{name}: changed bytes={changed}, max delta={worst}, "
            + $"ordinary={pair.Split.Work.PointStaticRebuilds}, fallback={pair.Split.Work.PointRigidFallbackRowsRendered}, "
            + $"transition={pair.Split.Work.PointRigidTransitionRowsRendered}, "
            + $"transient={pair.Split.Work.PointTransientRowsRendered}, draws={pair.Split.Work.PointFaceDrawCalls}, "
            + $"base=[{string.Join(',', pair.Split.BaseRows)}], full=[{string.Join(',', pair.Full.BaseRows)}], "
            + $"current=[{string.Join(',', pair.Split.TransientRows)}], "
            + $"current-rigid={pair.Split.Work.PointStaticTransientRigidDrawCalls}, "
            + $"current-skinned={pair.Split.Work.PointStaticTransientSkinnedDrawCalls}, "
            + $"fallback-skinned={pair.Split.Work.PointStaticFallbackSkinnedDrawCalls}");
        if (changed != 0 && !_capturedFailure)
        {
            _capturedFailure = true;
            try { output.WriteLine($"Failure evidence: {fixture.SaveFailure(testCase, name, pair)}"); }
            catch (Exception error) { output.WriteLine($"Failure evidence capture failed: {error}"); }
        }
        Assert.True(changed == 0, $"{name}: {changed} differing bytes, max delta {worst}");
        return pair;
    }
}
