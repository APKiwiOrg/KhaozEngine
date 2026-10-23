using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class DqHistoryRejectionTests
{
    // Reintroducing an unconditional mask target or its bind/clear makes each empty-path case fail.
    [Theory]
    [InlineData("empty", false)]
    [InlineData("unchanged", false)]
    [InlineData("offscreen", false)]
    [InlineData("after-active", false)]
    [InlineData("empty", true)]
    [InlineData("unchanged", true)]
    [InlineData("offscreen", true)]
    [InlineData("after-active", true)]
    public void No_projected_rejection_records_no_mask_traffic(string scenario, bool split)
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        RejectionWitness.Configure(scene);
        scene.TemporalResolveEntryForTests = split ? TemporalResolveEntry.Split : TemporalResolveEntry.Fused;
        var factory = (FakeGpuResourceFactory)harness.Device.Factory;
        SkinnedGltfMesh mesh = DqMotionWitness.Patch(SkinningProfile.DualQuaternionRigid);
        SkinnedMeshHandle handle = scene.LoadSkinnedMesh(mesh);
        float x = scenario == "offscreen" ? 10000 : 0;
        using var first = Frame();
        Assert.Equal((0, 0, 0), (first.Binds, first.Clears, first.Draws));
        Assert.Empty(MaskTargets()); // No previous history, even when an actor is queued.
        if (scenario is "offscreen" or "after-active")
        {
            scene.UnloadSkinnedMesh(handle);
            mesh = DqMotionWitness.Patch(SkinningProfile.LinearBlend);
            handle = scene.LoadSkinnedMesh(mesh);
        }
        IGpuTexture? retained = null;
        if (scenario == "after-active")
        {
            using var active = Frame();
            Assert.Equal((1, 1, 1), (active.Binds, active.Clears, active.Draws));
            retained = Assert.Single(MaskTargets());
        }
        using var empty = Frame();
        Assert.Equal((0, 0, 0), (empty.Binds, empty.Clears, empty.Draws));
        if (retained is null) Assert.Empty(MaskTargets());
        else Assert.Same(retained, Assert.Single(MaskTargets()));
        var set = Assert.IsType<FakeResourceSet>(scene.TemporalResolveRendererForTests!.CurrentSet);
        var zero = Assert.IsType<FakeTexture>(set.Resources[^1]);
        Assert.Equal((1u, 1u), (zero.Width, zero.Height));
        Assert.Equal(GpuTextureUsage.Sampled, zero.Usage);

        FakeTexture[] MaskTargets() => factory.Textures.Where(t => t.Format == GpuPixelFormat.R8UNorm
            && (t.Usage & GpuTextureUsage.RenderTarget) != 0).ToArray();
        RejectionMaskCommands Frame()
        {
            scene.Begin();
            if (scenario != "empty") DqMotionWitness.Queue(scene, mesh, handle, MotionKey.From(1),
                Matrix4x4.CreateTranslation(x, 0, 0), DqMotionWitness.Current, 0);
            scene.PrepareFrame();
            var commands = new RejectionMaskCommands();
            scene.RenderInternal(commands, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
            return commands;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Warm_zero_and_active_resolve_sets_survive_both_transitions(bool split)
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        var history = new TemporalHistory();
        history.EnsureTargets(device, 32, 24, 32, 24);
        using var retired = GpuRetireQueue.Create(device);
        using var renderer = new TemporalResolveRenderer(device);
        renderer.SelectEntry(split ? TemporalResolveEntry.Split : TemporalResolveEntry.Fused, retired);
        using var colour = Input(GpuPixelFormat.R16G16B16A16Float);
        using var depth = Input(GpuPixelFormat.R32Float);
        using var motion = Input(GpuPixelFormat.R16G16Float);
        using var mask = Input(GpuPixelFormat.R8UNorm);
        var inputs = new TemporalResolveInputs(colour, colour, depth, motion);
        using var commands = RejectionWitness.Commands();
        try
        {
            IGpuResourceSet zero = Bind(null);
            IGpuResourceSet active = Bind(mask);
            int sets = factory.ResourceSets.Count, textures = factory.Textures.Count, framebuffers = factory.Framebuffers.Count;
            Assert.Same(zero, Bind(null));
            Assert.Same(active, Bind(mask));
            Assert.Same(zero, Bind(null));
            Assert.Equal(sets, factory.ResourceSets.Count);
            Assert.Equal(textures, factory.Textures.Count);
            Assert.Equal(framebuffers, factory.Framebuffers.Count);
        }
        finally { renderer.ReleaseSets(retired); history.ReleaseTargets(retired); }

        IGpuTexture Input(GpuPixelFormat format) => device.Factory.CreateTexture(
            GpuTextureDescription.Texture2D(32, 24, format, GpuTextureUsage.Sampled));
        IGpuResourceSet Bind(IGpuTexture? rejection)
        {
            renderer.BindInputs(inputs with { Rejection = rejection }, history, retired);
            renderer.Run(commands, history);
            return renderer.CurrentSet!;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Profile_or_source_change_projects_only_its_old_and_current_bounds(bool gpu, bool method)
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        RejectionWitness.Configure(scene);
        scene.UseGpuSkinning = gpu;
        SkinnedGltfMesh mesh = DqMotionWitness.Patch(SkinningProfile.DualQuaternionRigid);
        SkinnedMeshHandle a = scene.LoadSkinnedMesh(mesh), b = scene.LoadSkinnedMesh(mesh);
        scene.Begin();
        Queue(a, mesh, 1, -2);
        Queue(b, mesh, 2, 3);
        harness.Render();
        scene.UnloadSkinnedMesh(a);
        var replacement = DqMotionWitness.Patch(method ? SkinningProfile.LinearBlend : SkinningProfile.DualQuaternionRigid);
        SkinnedMeshHandle next = scene.LoadSkinnedMesh(replacement);
        Assert.Equal(a.Index, next.Index);
        Assert.NotEqual(a.Generation, next.Generation);
        scene.RenderOrigin = new Vector3(128, 0, 128);
        scene.Camera.Target += new Vector3(.125f, 0, 0);
        scene.Begin();
        Queue(next, replacement, 1, 0);
        Queue(b, mesh, 2, 3);
        scene.PrepareFrame();
        using var commands = RejectionWitness.Commands();
        scene.RenderInternal(commands, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
        Assert.NotNull(scene.PreviousFrameView);
        Vector2[] vertices = RejectionWitness.Vertices(commands);
        Assert.Equal(1f, AtWorld(-2));
        Assert.Equal(1f, AtWorld(0));
        Assert.Equal(0f, AtWorld(3));
        var owner = RejectionWitness.SceneMask(scene);
        Assert.NotNull(owner);
        var resolveSet = Assert.IsType<FakeResourceSet>(scene.TemporalResolveRendererForTests!.CurrentSet);
        Assert.Same(RejectionWitness.Texture(owner), resolveSet.Resources[^1]);
        commands.Clear();
        scene.Camera.Target += Vector3.UnitX;
        scene.RenderInternal(commands, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
        Assert.False(scene.ResolvedLastRenderForTests);
        Assert.Empty(RejectionWitness.Vertices(commands));
        Assert.Same(owner, RejectionWitness.SceneMask(scene));
        scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        scene.Begin();
        harness.Render();
        Assert.Null(RejectionWitness.SceneMask(scene));

        void Queue(SkinnedMeshHandle handle, SkinnedGltfMesh source, ulong key, float x) =>
            DqMotionWitness.Queue(scene, source, handle, MotionKey.From(key), Matrix4x4.CreateTranslation(x, 0, 0), DqMotionWitness.Current, 0);
        float AtWorld(float x)
        {
            Vector4 clip = Vector4.Transform(new Vector4(new Vector3(x, 0, 0) - scene.CurrentFrameView.RenderOrigin, 1),
                scene.CurrentFrameView.ViewProjection);
            return RejectionWitness.At(vertices, new Vector2(clip.X, clip.Y) / clip.W);
        }
    }

    [Fact]
    public void Absent_actor_rejects_current_bounds_and_a_corresponding_actor_keeps_history()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        RejectionWitness.Configure(scene);
        SkinnedGltfMesh mesh = DqMotionWitness.Patch(SkinningProfile.LinearBlend);
        var handle = scene.LoadSkinnedMesh(mesh);
        scene.Begin();
        harness.Render();
        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(handle, Matrix4x4.Identity) { Motion = MotionKey.From(1) }, mesh.RestPose);
        scene.PrepareFrame();
        using var commands = RejectionWitness.Commands();
        scene.RenderInternal(commands, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
        Assert.Equal(1f, RejectionWitness.At(RejectionWitness.Vertices(commands), Vector2.Zero));
        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(handle, Matrix4x4.Identity) { Motion = MotionKey.From(1) }, mesh.RestPose);
        scene.PrepareFrame();
        commands.Clear();
        scene.RenderInternal(commands, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
        Assert.Empty(RejectionWitness.Vertices(commands));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unbounded_previous_or_current_box_rejects_the_whole_view(bool previous)
    {
        using var device = new FakeGpuDevice();
        using var mask = new RejectionWitness.Mask(device);
        var unbounded = new MeshBounds(new Vector3(float.NegativeInfinity), new Vector3(float.PositiveInfinity));
        mask.Add(previous ? null : unbounded, previous ? unbounded : null);
        using var commands = RejectionWitness.Commands();
        mask.Prepare(commands, RejectionWitness.View(), RejectionWitness.View());
        Vector2[] vertices = RejectionWitness.Vertices(commands);
        foreach (Vector2 point in new[] { new Vector2(-.99f, -.99f), new Vector2(.99f, .99f), Vector2.Zero })
            Assert.Equal(1f, RejectionWitness.At(vertices, point));
    }

    [Fact]
    public void Near_plane_crossing_and_viewport_edges_are_conservative_and_clear_reuses_storage()
    {
        using var device = new FakeGpuDevice();
        using var mask = new RejectionWitness.Mask(device);
        Matrix4x4 lens = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 10);
        FrameView view = RejectionWitness.View(lens);
        // At the near plane the clipped box projects from x=.1 to beyond the right edge.
        mask.Add(new MeshBounds(new Vector3(.01f, -.02f, -.2f), new Vector3(.2f, .02f, .1f)), null);
        using var commands = RejectionWitness.Commands();
        mask.Prepare(commands, view, null);
        Vector2[] vertices = RejectionWitness.Vertices(commands);
        Assert.Equal(1f, RejectionWitness.At(vertices, new Vector2(.99f, 0)));
        Assert.Equal(1f, RejectionWitness.At(vertices, new Vector2(.11f, .19f)));
        Assert.Equal(0f, RejectionWitness.At(vertices, new Vector2(-.9f, 0)));
        var texture = mask.Texture;
        Assert.NotNull(texture);
        mask.Clear();
        commands.Clear();
        mask.Prepare(commands, view, null);
        Assert.Null(mask.Texture);
        Assert.Equal(0, commands.FramebufferBinds);
        Assert.Empty(commands.ColourClears);
        Assert.Empty(RejectionWitness.Vertices(commands));
        mask.Add(new MeshBounds(new Vector3(-.01f, -.01f, -.3f), new Vector3(.01f, .01f, -.2f)), null);
        var resized = new FrameView(view.View, view.Projection, view.ViewProjection, view.AbsoluteViewProjection,
            view.RenderOrigin, 200, 100, 2, Vector2.Zero);
        mask.Prepare(commands, resized, null);
        Assert.NotSame(texture, mask.Texture);
        Assert.False(Assert.IsType<FakeTexture>(texture).Disposed);
        var replacement = Assert.IsType<FakeTexture>(mask.Texture);
        mask.Dispose();
        Assert.True(Assert.IsType<FakeTexture>(texture).Disposed);
        Assert.True(replacement.Disposed);
    }

    [Fact]
    public void Previous_camera_and_absolute_origin_project_the_old_region_outward()
    {
        using var device = new FakeGpuDevice();
        using var mask = new RejectionWitness.Mask(device);
        FrameView now = RejectionWitness.View(origin: new Vector3(128, 0, 128));
        FrameView previous = RejectionWitness.View(view: Matrix4x4.CreateTranslation(.5f, 0, 0), origin: new Vector3(128, 0, 128));
        mask.Add(null, new MeshBounds(new Vector3(127.8f, -.1f, 128.2f), new Vector3(128, .1f, 128.3f)));
        using var commands = RejectionWitness.Commands();
        mask.Prepare(commands, now, previous);
        Vector2[] vertices = RejectionWitness.Vertices(commands);
        Assert.Equal(1f, RejectionWitness.At(vertices, new Vector2(-.1f, 0)));
        Assert.Equal(1f, RejectionWitness.At(vertices, new Vector2(.4f, 0)));
        Assert.Equal(0f, RejectionWitness.At(vertices, new Vector2(-.8f, 0)));
    }
}

internal static class RejectionWitness
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static object? SceneMask(Scene3D scene) => typeof(Scene3D).GetField("_skinningHistoryRejection", Private)?.GetValue(scene);
    internal static IGpuTexture? Texture(object? mask) => (IGpuTexture?)mask?.GetType().GetProperty("Texture")?.GetValue(mask);
    internal static void Configure(Scene3D scene)
    {
        DqMotionScene.Configure(scene);
        scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
    }
    internal static RecordingGpuCommandList Commands() => new(new NullGpuCommandList()) { CaptureBindings = true, CapturePayloads = true };
    internal static FrameView View(Matrix4x4? projection = null, Matrix4x4? view = null, Vector3 origin = default)
    {
        Matrix4x4 v = view ?? Matrix4x4.Identity, p = projection ?? Matrix4x4.Identity;
        return new(v, p, v * p, Matrix4x4.CreateTranslation(-origin) * v * p, origin, 100, 100, 1, Vector2.Zero);
    }
    internal static Vector2[] Vertices(RecordingGpuCommandList commands)
    {
        var draw = commands.IndexedDraws.SingleOrDefault(d => d.Pipeline is FakePipeline { Request: { } p }
            && p.FragmentGlsl.Contains("oRejection", StringComparison.Ordinal));
        if (draw.VertexBuffer is null) return [];
        byte[] data = Assert.Single(commands.Uploads, u => ReferenceEquals(u.Buffer, draw.VertexBuffer)).Data!;
        return MemoryMarshal.Cast<byte, Vector2>(data).ToArray();
    }
    internal static float At(Vector2[] vertices, Vector2 point)
    {
        for (int i = 0; i < vertices.Length; i += 4)
            if (point.X >= vertices[i].X && point.X <= vertices[i + 2].X
                && point.Y >= vertices[i].Y && point.Y <= vertices[i + 2].Y) return 1;
        return 0;
    }

    // The pre-feature baseline has no mask. It means zero rejection, not a compilation failure at a missing type.
    internal sealed class Mask : IDisposable
    {
        object? _mask;
        internal Mask(IGpuDevice device)
        {
            Type? type = typeof(Scene3D).Assembly.GetType("KhaozEngine.Render3D.Rendering.TemporalHistoryRejection");
            if (type is not null) _mask = Activator.CreateInstance(type, device);
        }
        internal IGpuTexture? Texture => RejectionWitness.Texture(_mask);
        internal void Add(MeshBounds? current, MeshBounds? previous) => Call("Add", current, previous);
        internal void Clear() => Call("Clear");
        internal void Prepare(IGpuCommandList commands, FrameView current, FrameView? previous) => Call("Prepare", commands, current, previous);
        void Call(string name, params object?[] args) => _mask?.GetType().GetMethod(name)!.Invoke(_mask, args);
        public void Dispose()
        {
            (_mask as IDisposable)?.Dispose();
            _mask = null;
        }
    }
}

// Counts mask framebuffer operations even when no draw follows the clear.
internal sealed class RejectionMaskCommands : IGpuCommandList
{
    bool _maskBound;
    internal int Binds, Clears, Draws;
    public void Begin() { }
    public void End() { }
    public void SetFramebuffer(IGpuFramebuffer fb)
    {
        _maskBound = fb is FakeFramebuffer f && f.Colour.Any(t => t.Format == GpuPixelFormat.R8UNorm);
        if (_maskBound) Binds++;
    }
    public void ClearColorTarget(uint index, Color rgba) { if (_maskBound) Clears++; }
    public void ClearDepthStencil(float depth) { }
    public void SetPipeline(IGpuPipeline p) { }
    public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set) { }
    public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) { }
    public void SetVertexBuffer(uint slot, IGpuBuffer b) { }
    public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes) { }
    public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt) { }
    public void SetScissorRect(uint index, uint x, uint y, uint w, uint h) { }
    public void SetFullScissorRects() { }
    public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart) { if (_maskBound) Draws++; }
    public void Draw(uint vertexCount) { if (_maskBound) Draws++; }
    public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart) { if (_maskBound) Draws++; }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged { }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged { }
    public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes) { }
    public void CopyTexture(IGpuTexture src, IGpuTexture dst) { }
    public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst, uint width, uint height) { }
    public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
        IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
    { }
    public void GenerateMipmaps(IGpuTexture texture) { }

    public void ResolveTexture(IGpuTexture src, IGpuTexture dst) { }
    public void SetComputePipeline(IGpuComputePipeline p) { }
    public void SetComputeResourceSet(uint slot, IGpuResourceSet set) { }
    public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) { }
    public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) { }
    public void Dispose() { }
}
