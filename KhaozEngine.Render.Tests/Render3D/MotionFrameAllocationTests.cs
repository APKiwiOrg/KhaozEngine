using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class MotionFrameAllocationTests(ITestOutputHelper output)
{
    [Fact]
    public void PreparingASteadyFramesMotionAllocatesNothing()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
        for (int n = 0; n < 4; n++)   // the history, the slots and the buffers reach their steady size
        {
            scene.Begin();
            for (int i = 0; i < 32; i++)
                scene.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(i, .1f * n, 0f)) { Motion = MotionKey.From((ulong)i + 1) });
            scene.Draw(box, Matrix4x4.CreateTranslation(0f, 0f, 3f));
            harness.Render();
        }
        Assert.NotNull(scene.MotionResourcesForTests);

        var commands = new NullGpuCommandList();
        AllocAssert.NoPerCallAllocation("PrepareMotionFrame over 20 calls", () =>
        {
            for (int i = 0; i < 20; i++) scene.PrepareMotionFrame(commands);
        });
    }

    // The skinned and foliage motion runs inside the frame, not in PrepareMotionFrame, so this reading renders whole
    // frames: keyed and unkeyed rigid, a keyed and an unkeyed body under the skinning path the row names, wind-blown
    // foliage and a beam.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASteadyTemporalFrameOverEveryPathAllocatesNothing(bool gpuSkinning)
    {
        const int Warm = 8, Measured = 16;
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.UseGpuSkinning = gpuSkinning;
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(.5f));
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);
        Matrix4x4[] pose = MotionTestScene.Bent(mesh, .3f);
        using FoliageBatch grass = scene.CreateFoliageBatch(new[] { new FoliageInstance(box, Matrix4x4.CreateTranslation(1f, 0f, 1f), .1f) });
        var wind = new FoliageRenderSettings { DrawRadius = 100f, DistantDensity = 1f, WindStrength = .4f };
        var commands = new NullGpuCommandList();
        int n = 0;
        void Frame()
        {
            scene.Begin();
            scene.EffectTimeSeconds = n / 60f;
            scene.Camera.Target = new Vector3(.01f * n, 0f, 0f);
            for (int i = 0; i < 16; i++)
                scene.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(.25f * i - 2f, 0f, .01f * n)) { Motion = MotionKey.From((ulong)i + 1) });
            scene.Draw(box, Matrix4x4.CreateTranslation(0f, 0f, -1f));
            scene.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.CreateTranslation(-1f, 0f, .01f * n)) { Motion = MotionKey.From(100) }, pose);
            scene.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.CreateTranslation(1f, 0f, 0f)), pose);
            scene.DrawFoliage(grass, Vector3.Zero, wind);
            scene.DrawBeam(new Vector3(-2f, 1f, 0f), new Vector3(2f, 1f, 0f), .2f, Color.White);
            scene.PrepareFrame();
            scene.RenderInternal(commands, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
            n++;
        }
        void Frames(int count) { for (int i = 0; i < count; i++) Frame(); }

        Frames(Warm);
        long without = Allocated(() => Frames(Measured));
        output.WriteLine($"{Measured} steady headless frames without temporal rendering: {without} bytes");

        scene.ForceTemporalForTests = true;
        Frames(Warm);   // both history generations, the slots and every grow-only buffer reach their steady size
        Assert.NotNull(scene.MotionResourcesForTests);
        AllocAssert.NoPerCallAllocation($"{Measured} steady temporal frames over every path", () => Frames(Measured));
        AssertEveryPathRan(scene, gpuSkinning);
    }

    /// <summary>The last frame had history with a last frame for the keyed box and body, drew both bodies through the
    /// skinning path the row names, the keyed one first, and submitted foliage, so the reading covered every temporal
    /// path.</summary>
    internal static void AssertEveryPathRan(Scene3D scene, bool gpuSkinning)
    {
        Assert.NotNull(scene.PreviousFrameView);
        MotionHistory history = scene.ActiveMotionHistory!;
        Assert.True(history.TryGetPreviousRigid(MotionKey.From(1), out _), "the keyed box has no last frame");
        Assert.True(history.TryGetPreviousSkinned(MotionKey.From(100), out _, out _), "the keyed body has no last frame");
        Assert.Equal(2, scene.DrawnSkinnedInstances);
        MotionKey first = gpuSkinning ? scene.GpuSkinnedMotionForTests(0) : scene.CpuSkinnedMotionForTests(0).Motion;
        MotionKey second = gpuSkinning ? scene.GpuSkinnedMotionForTests(1) : scene.CpuSkinnedMotionForTests(1).Motion;
        Assert.Equal(MotionKey.From(100), first);
        Assert.True(second.IsNone);
        Assert.True(scene.LastFoliageStats.SubmittedPatches > 0, "no foliage patch was submitted");
    }

    static long Allocated(Action frames)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        frames();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
