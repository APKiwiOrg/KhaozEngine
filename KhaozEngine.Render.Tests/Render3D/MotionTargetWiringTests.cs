using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>The frame allocates the motion target, rebuilds every model-pass pipeline and uploads the motion state
/// exactly while temporal rendering is active, and creates none of it otherwise.</summary>
public sealed class MotionTargetWiringTests
{
    static RecordingGpuCommandList Frame(MotionTestScene harness, Action<Scene3D> queue)
    {
        harness.Scene.Begin();
        queue(harness.Scene);
        harness.Scene.PrepareFrame();
        var cl = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        harness.Scene.RenderInternal(cl, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
        return cl;
    }

    static byte[] Payload(RecordingGpuCommandList cl, IGpuBuffer buffer)
    {
        RecordingGpuCommandList.Upload upload = cl.Uploads.Last(u => ReferenceEquals(u.Buffer, buffer));
        return upload.Data!.AsSpan(0, (int)upload.Bytes).ToArray();
    }

    [Fact]
    public void TheMotionTargetExistsExactlyWhileTemporalRenderingIsActive()
    {
        using var harness = new MotionTestScene();
        var factory = (FakeGpuResourceFactory)harness.Device.Factory;
        MeshHandle box = harness.Scene.LoadMesh(MeshPrimitives.Box(1f));
        void Queue(Scene3D s) => s.Draw(box, Matrix4x4.Identity);

        Frame(harness, Queue).Dispose();
        Frame(harness, Queue).Dispose();
        Assert.Throws<InvalidOperationException>(() => harness.Scene.ReadMotionTargetForTests());   // no target
        Assert.DoesNotContain(factory.GraphicsPipelines, p => p.FragmentGlsl.Contains("oMotion", StringComparison.Ordinal));
        Assert.Null(harness.Scene.MotionResourcesForTests);
        int texturesBefore = factory.Textures.Count;

        harness.Scene.ForceTemporalForTests = true;
        Frame(harness, Queue).Dispose();
        // The resize recreates every target. Other passes own RG16F textures too (distortion, target outlines), but
        // none of them is recreated by it, so the motion target is the one new RG16F texture.
        FakeTexture motion = Assert.Single(factory.Textures.Skip(texturesBefore), t => t.Format == GpuPixelFormat.R16G16Float);
        Assert.NotNull(harness.Scene.MotionResourcesForTests);
        Assert.Contains(factory.GraphicsPipelines, p => p.VertexGlsl == ShaderSources.ModelMotionVert);

        harness.Scene.ForceTemporalForTests = false;
        Frame(harness, Queue).Dispose();
        Assert.True(motion.Disposed);
        Assert.Null(harness.Scene.MotionResourcesForTests);
    }

    [Fact]
    public void ATemporalFrameUploadsTheMotionBlockAndARigidSlotPerInstance()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        scene.RenderOrigin = Vector3.Zero;   // so the previous transform reads back as the absolute one
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
        MotionKey key = MotionKey.From(3);
        Action<Scene3D> At(float x) => s =>
        {
            s.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(x, 0f, 0f)) { Motion = key });
            s.Draw(box, Matrix4x4.CreateTranslation(0f, 2f, 0f));
        };

        using RecordingGpuCommandList first = Frame(harness, At(0f));
        ModelMotionResources motion = scene.MotionResourcesForTests!;
        MotionFrameUbo firstBlock = MemoryMarshal.Read<MotionFrameUbo>(Payload(first, motion.FrameBuffer));
        Assert.Equal(0f, firstBlock.Params.X);   // no history yet
        Assert.Equal(firstBlock.CurViewProj, firstBlock.PrevViewProj);   // so last frame's matrix is this frame's

        using RecordingGpuCommandList second = Frame(harness, At(1f));
        MotionFrameUbo block = MemoryMarshal.Read<MotionFrameUbo>(Payload(second, motion.FrameBuffer));
        Assert.Equal(1f, block.Params.X);
        Assert.Equal(scene.CurrentFrameView.ViewProjection, block.CurViewProj);
        Assert.Equal(scene.PreviousFrameView!.Value.ViewProjection, block.PrevViewProj);
        // One run of two slots in submission order: the keyed box, then the unkeyed one.
        Assert.Equal(new[] { 0f, -1f }, MemoryMarshal.Cast<byte, float>(Payload(second, motion.SlotBuffer)).ToArray()[..2]);
        Assert.Equal(Matrix4x4.Identity, MemoryMarshal.Read<Matrix4x4>(Payload(second, motion.PreviousTransforms)));
    }

    [Fact]
    public void TheMotionBlockAndTheRigidSlotsGoUpOncePerFrameBeforeItsFirstDraw()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);
        void Queue(Scene3D s)
        {
            s.Draw(box, Matrix4x4.Identity);
            s.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.CreateTranslation(1f, 0f, 0f)), mesh.RestPose);
        }

        Frame(harness, Queue).Dispose();
        using RecordingGpuCommandList cl = Frame(harness, Queue);
        ModelMotionResources motion = scene.MotionResourcesForTests!;

        // Every temporal draw reads the block, and the rigid ones the slots, so both precede the frame's first draw.
        Assert.True(cl.DrawCount > 0, "the frame drew nothing, so the ordering proves nothing");
        Assert.Equal(0, Assert.Single(cl.Uploads, u => ReferenceEquals(u.Buffer, motion.FrameBuffer)).DrawsBefore);
        Assert.Equal(0, Assert.Single(cl.Uploads, u => ReferenceEquals(u.Buffer, motion.SlotBuffer)).DrawsBefore);
    }

    [Fact]
    public void ASecondRenderInsideAFrameUploadsTheSameHistoryAndItsOwnRigidSlots()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
        MotionKey key = MotionKey.From(5);
        Action<Scene3D> At(float x) => s =>
            s.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(x, 0f, 0f)) { Motion = key });
        Frame(harness, At(0f)).Dispose();

        using RecordingGpuCommandList first = Frame(harness, At(1f));   // valid history
        ModelMotionResources motion = scene.MotionResourcesForTests!;
        MotionFrameUbo block = MemoryMarshal.Read<MotionFrameUbo>(Payload(first, motion.FrameBuffer));
        Assert.Equal(1f, block.Params.X);

        // An offscreen capture inside the same frame: no Begin, so the frame keeps its history, keys and instances.
        using var second = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        scene.PrepareFrame();
        scene.RenderInternal(second, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
        MotionFrameUbo again = MemoryMarshal.Read<MotionFrameUbo>(Payload(second, motion.FrameBuffer));
        Assert.Equal(1f, again.Params.X);
        Assert.Equal(block.PrevViewProj, again.PrevViewProj);
        Assert.Equal(0f, MemoryMarshal.Read<float>(Payload(second, motion.SlotBuffer)));   // the keyed box's last frame
    }

    // One frame recorded with every indexed draw's bindings, the draws whose vertex program is one of `vertex`.
    static RecordingGpuCommandList.DrawBindings[] DrawsOf(MotionTestScene harness, Action<Scene3D> queue,
        params string[] vertex)
    {
        harness.Scene.Begin();
        queue(harness.Scene);
        harness.Scene.PrepareFrame();
        using var cl = new RecordingGpuCommandList(new NullGpuCommandList()) { CaptureBindings = true };
        harness.Scene.RenderInternal(cl, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
        return cl.Bindings.Where(d => d.Pipeline is FakePipeline { Request: { } r } && vertex.Contains(r.VertexGlsl))
            .ToArray();
    }

    [Fact]
    public void RigidAndGpuSkinnedDrawsBindTheirMotionStateExactlyWhileTheTargetIsTemporal()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);
        void Queue(Scene3D s)
        {
            s.Draw(box, Matrix4x4.Identity);
            s.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.CreateTranslation(1f, 0f, 0f)), mesh.RestPose);
            s.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.CreateTranslation(-1f, 0f, 0f)), mesh.RestPose);
        }

        RecordingGpuCommandList.DrawBindings[] off = DrawsOf(harness, Queue, ShaderSources.ModelVert,
            ShaderSources.SkinnedModelVert);
        Assert.Equal(3, off.Length);
        Assert.Equal(new uint[] { 0 }, off[0].Sets.Keys.Order().ToArray());          // rigid: the material alone
        Assert.Equal(new uint[] { 0, 1 }, off[0].VertexBuffers.Keys.Order().ToArray());
        Assert.All(off[1..], d => Assert.Equal(new uint[] { 0, 1, 2 }, d.Sets.Keys.Order().ToArray()));

        scene.ForceTemporalForTests = true;
        RecordingGpuCommandList.DrawBindings[] on = DrawsOf(harness, Queue, ShaderSources.ModelMotionVert,
            ShaderSources.SkinnedModelMotionVert);
        ModelMotionResources motion = scene.MotionResourcesForTests!;
        Assert.Equal(3, on.Length);
        Assert.Same(motion.RigidSet, on[0].Sets[1].Set);
        Assert.Same(motion.SlotBuffer, on[0].VertexBuffers[2]);
        for (uint slot = 0; slot < 2; slot++)
        {
            (IGpuResourceSet set, uint offset) = on[1 + slot].Sets[3];
            Assert.Same(motion.SkinnedPalette.Set, set);
            Assert.Equal(SkinnedMotionPalette.OffsetFor(slot), offset);
        }
    }

    // The pipelines built against the model framebuffer: a depth attachment and at least the three base colour ones.
    static FakeGraphicsPipelineRequest[] ModelTargetPipelines(FakeGpuResourceFactory factory, int from) =>
        factory.GraphicsPipelines.Skip(from)
            .Where(p => p.Description.Outputs.Depth is not null && p.Description.Outputs.Colour.Length >= 3).ToArray();

    [Fact]
    public void AMotionToggleAtAFixedSizeRebuildsTheModelPassPipelinesForFourOutputsAndBackForThree()
    {
        using var harness = new MotionTestScene();
        var factory = (FakeGpuResourceFactory)harness.Device.Factory;
        MeshHandle box = harness.Scene.LoadMesh(MeshPrimitives.Box(1f));
        void Queue(Scene3D s) => s.Draw(box, Matrix4x4.Identity);
        Frame(harness, Queue).Dispose();
        Frame(harness, Queue).Dispose();   // the size, sample count and colour format are settled
        (int, int) size = (harness.Scene.CurrentFrameView.Width, harness.Scene.CurrentFrameView.Height);

        int built = factory.GraphicsPipelines.Count, idles = harness.Device.WaitForIdleCalls;
        harness.Scene.ForceTemporalForTests = true;
        Frame(harness, Queue).Dispose();
        FakeGraphicsPipelineRequest[] on = ModelTargetPipelines(factory, built);
        Assert.Equal(size, (harness.Scene.CurrentFrameView.Width, harness.Scene.CurrentFrameView.Height));
        Assert.Equal(1, harness.Scene.ModelSampleCountForTests);
        Assert.True(harness.Device.WaitForIdleCalls > idles, "the toggle recreated the model target without idling the device");
        Assert.All(on, p => Assert.Equal(4, p.Description.Outputs.Colour.Length));
        foreach (string vertex in new[] { ShaderSources.ModelMotionVert, ShaderSources.ModelCpuSkinnedMotionVert,
            ShaderSources.SkinnedModelMotionVert, ShaderSources.SplatMotionVert, ShaderSources.TileGroundMotionVert })
            Assert.Contains(on, p => p.VertexGlsl == vertex);
        foreach (string fragment in new[] { ShaderSources.TexturedBillboardMotionFrag, ShaderSources.BeamMotionFrag,
            ShaderSources.TrailMotionFrag, ShaderSources.OverlayUnlitMotionFrag, ShaderSources.SilhouetteMotionFrag })
            Assert.Contains(on, p => p.FragmentGlsl == fragment);

        built = factory.GraphicsPipelines.Count;
        idles = harness.Device.WaitForIdleCalls;
        harness.Scene.ForceTemporalForTests = false;
        Frame(harness, Queue).Dispose();
        FakeGraphicsPipelineRequest[] off = ModelTargetPipelines(factory, built);
        Assert.Equal(size, (harness.Scene.CurrentFrameView.Width, harness.Scene.CurrentFrameView.Height));
        Assert.True(harness.Device.WaitForIdleCalls > idles, "the toggle recreated the model target without idling the device");
        Assert.All(off, p =>
        {
            Assert.Equal(3, p.Description.Outputs.Colour.Length);
            Assert.DoesNotContain("oMotion", p.FragmentGlsl, StringComparison.Ordinal);
        });
        Assert.Contains(off, p => p.VertexGlsl == ShaderSources.ModelVert && p.FragmentGlsl == ShaderSources.ModelFrag);
        Assert.Contains(off, p => p.FragmentGlsl == ShaderSources.TexturedBillboardFrag);
    }
}
