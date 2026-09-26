using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("AllocSensitive")]
public sealed class MotionFrameAllocationGpuTests(ITestOutputHelper output)
{
    const int W = 320, H = 180, Warm = 8, Measured = 16;

    [GpuTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASteadyTemporalFrameAllocatesNoMoreThanTheSameFrameWithoutIt(bool gpuSkinning)
    {
        using var fx = new TemporalFixture(W, H, s =>
        {
            s.UseGpuSkinning = gpuSkinning;
            s.Camera.OrthoSize = 10f;
        });
        Scene3D scene = fx.Scene;
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
        SkinnedGltfMesh mesh = SkinnedMeshBuilder.BuildTube(.2f, 1.2f, 6, 4, 3, Axis.Y);
        SkinnedMeshHandle tube = scene.LoadSkinnedMesh(mesh);
        Matrix4x4[] pose = MotionTestScene.Bent(mesh, .3f);
        MeshHandle blade = scene.LoadMesh(MeshPrimitives.Box(.2f));
        using FoliageBatch grass = scene.CreateFoliageBatch(new[] { new FoliageInstance(blade, Matrix4x4.CreateTranslation(2f, 0f, 2f), .1f) });
        var wind = new FoliageRenderSettings { DrawRadius = 100f, DistantDensity = 1f, WindStrength = .4f };
        Action<Scene3D, int> draw = (s, n) =>
        {
            s.Camera.Target = new Vector3(.05f * n, 0f, 0f);
            for (int i = 0; i < 16; i++)
                s.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(i - 8f, .5f, .02f * n)) { Motion = MotionKey.From((ulong)i + 1) });
            s.Draw(box, Matrix4x4.CreateTranslation(0f, .5f, -3f));
            s.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.CreateTranslation(-2f, 0f, 1f)) { Motion = MotionKey.From(100) }, pose);
            s.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.CreateTranslation(2f, 0f, -1f)), pose);   // unkeyed, so its last frame is its own
            s.DrawFoliage(grass, Vector3.Zero, wind);
            s.DrawBeam(new Vector3(-4f, 1f, 0f), new Vector3(4f, 1f, 0f), .3f, Color.White);
        };

        fx.Frames(Warm, draw);
        long without = Allocated(() => fx.Frames(Measured, draw));

        scene.ForceTemporalForTests = true;
        fx.Frames(Warm, draw);   // the first temporal frames build the target, the variants and the history
        Assert.NotNull(scene.MotionResourcesForTests);
        long with = Allocated(() => fx.Frames(Measured, draw));
        if (with > without) with = Allocated(() => fx.Frames(Measured, draw));   // one retry, as AllocAssert allows

        output.WriteLine($"{Measured} steady frames: {without} bytes without temporal rendering, {with} bytes with it");
        MotionFrameAllocationTests.AssertEveryPathRan(scene, gpuSkinning);
        Assert.True(with <= without,
            $"{Measured} steady temporal frames allocated {with} bytes, and the same frames without temporal rendering {without}");
    }

    static long Allocated(Action frames)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        frames();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
