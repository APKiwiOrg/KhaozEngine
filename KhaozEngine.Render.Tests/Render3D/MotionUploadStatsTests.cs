using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>A temporal frame's streaming total counts the motion state it uploads, each piece in the bucket of the
/// stream it serves, and the buckets still partition the total. A frame with temporal rendering off counts none of
/// it.</summary>
public sealed class MotionUploadStatsTests
{
    static RecordingGpuCommandList Frame(MotionTestScene harness, Action<Scene3D> queue)
    {
        harness.Scene.Begin();
        queue(harness.Scene);
        harness.Scene.PrepareFrame();
        var cl = new RecordingGpuCommandList(new NullGpuCommandList());
        harness.Scene.RenderInternal(cl, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
        return cl;
    }

    static long UploadedTo(RecordingGpuCommandList cl, IGpuBuffer? buffer) =>
        cl.Uploads.Where(u => ReferenceEquals(u.Buffer, buffer)).Sum(u => (long)u.Bytes);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ATemporalFrameCountsExactlyTheMotionStateItUploads(bool gpuSkinning)
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.UseGpuSkinning = gpuSkinning;
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);
        void Queue(Scene3D s)
        {
            s.Draw(new RigidInstanceDraw(box, Matrix4x4.Identity) { Motion = MotionKey.From(1) });
            s.Draw(box, Matrix4x4.CreateTranslation(0f, 2f, 0f));
            s.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.CreateTranslation(1f, 0f, 0f)) { Motion = MotionKey.From(2) },
                mesh.RestPose);
        }

        Frame(harness, Queue).Dispose();
        Frame(harness, Queue).Dispose();
        RenderFrameStats off = scene.LastFrameStats;
        Assert.Equal(off.BufferUpdateBytes, off.UploadBytesPartitioned);

        scene.ForceTemporalForTests = true;
        Frame(harness, Queue).Dispose();
        using RecordingGpuCommandList cl = Frame(harness, Queue);   // valid history, so every key has a last frame
        RenderFrameStats on = scene.LastFrameStats;
        ModelMotionResources motion = scene.MotionResourcesForTests!;

        long rigid = UploadedTo(cl, motion.FrameBuffer) + UploadedTo(cl, motion.SlotBuffer)
            + UploadedTo(cl, motion.PreviousTransforms);
        long palette = UploadedTo(cl, motion.SkinnedPalette.BufferForTests);
        long cpuPrevious = gpuSkinning ? 0 : UploadedTo(cl, motion.CpuPrevious);
        Assert.True(rigid > 0, "the frame uploaded no rigid motion state, so the count proves nothing");
        Assert.True(gpuSkinning ? palette > 0 : cpuPrevious > 0, "the frame uploaded no skinned motion state");

        Assert.Equal(rigid, on.InstanceUploadBytes - off.InstanceUploadBytes);
        Assert.Equal(palette, on.SkinnedUniformUploadBytes - off.SkinnedUniformUploadBytes);
        Assert.Equal(cpuPrevious, on.SkinnedUploadBytes - off.SkinnedUploadBytes);
        Assert.Equal(rigid + palette + cpuPrevious, on.BufferUpdateBytes - off.BufferUpdateBytes);
        Assert.Equal(on.BufferUpdateBytes, on.UploadBytesPartitioned);
    }
}
