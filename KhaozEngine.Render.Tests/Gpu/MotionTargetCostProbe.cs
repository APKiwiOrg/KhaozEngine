using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// The engine-side cost of the motion target and the previous-state work at 1600x900
/// (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24, acceptance 5, read on Grimhollow's town path in round 3). A measurement,
/// not a gate: it prints the median frame with and without temporal rendering and asserts that both were measured and
/// that each block rendered the state it claims. Rounds alternate the two so thermal and clock drift land on both
/// alike.
/// </summary>
[Collection("GpuTimingSensitive")]
public sealed class MotionTargetCostProbe(ITestOutputHelper output)
{
    const int W = 1600, H = 900, Rounds = 4, WarmFrames = 12, TimedFrames = 40;
    const int Boxes = 400, Bodies = 8, Blades = 2500;

    [GpuFact(RequiresRealGpu = true)]
    public void TheMotionTargetCostIsMeasuredAt1600x900()
    {
        using var fx = new TemporalFixture(W, H, s => s.Camera.OrthoSize = 30f);
        Scene3D scene = fx.Scene;
        MeshHandle floor = scene.LoadMesh(MeshPrimitives.Plane(80f, 80f));
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
        SkinnedGltfMesh mesh = SkinnedMeshBuilder.BuildTube(.3f, 1.8f, 8, 6, 3, Axis.Y);
        SkinnedMeshHandle tube = scene.LoadSkinnedMesh(mesh);
        Matrix4x4[] pose = MotionTestScene.Bent(mesh, .3f);
        MeshHandle blade = scene.LoadMesh(MeshPrimitives.Cone(.05f, .5f, 4));
        var blades = new FoliageInstance[Blades];
        for (int i = 0; i < blades.Length; i++)
            blades[i] = new FoliageInstance(blade, Matrix4x4.CreateTranslation(i % 50 * .6f - 15f, 0f, i / 50 * .6f - 15f), (i * 37 % 100) / 100f);
        using FoliageBatch grass = scene.CreateFoliageBatch(blades);
        var wind = new FoliageRenderSettings { DrawRadius = 100f, DistantDensity = 1f, WindStrength = .4f };

        void Draw(Scene3D s, int n)
        {
            s.Camera.Target = new Vector3(.04f * n, 0f, .03f * n);
            s.Draw(floor, Matrix4x4.Identity);
            for (int i = 0; i < Boxes; i++)
                s.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(i % 20 * 1.5f - 15f, .5f + .02f * (n % 10), i / 20 * 1.5f - 15f))
                    { Motion = MotionKey.From((ulong)i + 1) });
            for (int i = 0; i < Bodies; i++)
                s.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.CreateTranslation(i * 3f - 12f, 0f, 8f))
                    { Motion = MotionKey.From(1000 + (ulong)i) }, pose);
            s.DrawFoliage(grass, s.Camera.Target, wind);
        }

        var off = new List<double>();
        var on = new List<double>();
        for (int round = 0; round < Rounds; round++)
            foreach (bool temporal in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                scene.ForceTemporalForTests = temporal;
                fx.Frames(WarmFrames, Draw);   // a switch rebuilds the target and the pipelines
                for (int i = 0; i < TimedFrames; i++)
                {
                    fx.Frames(1, Draw);
                    (temporal ? on : off).Add(fx.LastSubmitMilliseconds);
                }
                AssertTheBlockRendered(scene.LastTemporalDiagnostics, temporal, round);
            }

        double without = Median(off), with = Median(on);
        output.WriteLine($"motion target cost at {W}x{H}: {with - without:F3} ms added (median {with:F3} ms with temporal "
            + $"rendering, {without:F3} ms without, {on.Count} frames each) on {fx.Device.Capabilities.DeviceName}");
        Assert.True(double.IsFinite(with - without) && without > 0 && with > 0);
    }

    /// <summary>Read after a timed block, outside the timed window. A temporal block must end reprojecting with every
    /// keyed draw counted, and a block without it must end with no history, or the two medians time the same
    /// work.</summary>
    static void AssertTheBlockRendered(TemporalDiagnostics last, bool temporal, int round)
    {
        if (temporal)
        {
            Assert.True(last.HistoryValid, $"round {round}: the temporal block ended without a valid history");
            Assert.Equal(Boxes, last.KeyedRigid);
            Assert.Equal(Bodies, last.KeyedSkinned);
        }
        else
            Assert.False(last.HistoryValid,
                $"round {round}: the block without temporal rendering ended with a valid history");
    }

    static double Median(List<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
