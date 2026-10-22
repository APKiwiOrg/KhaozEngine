using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class DqHistoryRejectionTests
{
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
        Assert.Same(texture, mask.Texture);
        Assert.Empty(RejectionWitness.Vertices(commands));
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
        readonly object? _mask;
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
        public void Dispose() => (_mask as IDisposable)?.Dispose();
    }
}
