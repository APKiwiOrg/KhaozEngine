using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>A skinned mesh view draws part of a loaded skinned mesh in every pass, is refused with a typed reason
/// when its input is bad, holds its source while it lives and outlives its own unload until the frame ends.</summary>
public sealed partial class SkinnedMeshViewTests
{
    // Triangles 0 and 2 of Strip().
    static readonly uint[] AlternateTriangles = [0, 1, 2, 2, 3, 4];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_view_draws_only_its_indices_in_every_pass(bool gpuSkinning)
    {
        using var rig = new ViewRig(gpuSkinning);
        SkinnedMeshHandle source = rig.LoadStrip(out FakeBuffer sourceIndices);
        SkinnedMeshHandle view = rig.CreateView(source, AlternateTriangles, out FakeBuffer viewIndices);
        void Submit(Scene3D scene)
        {
            MeshOutlineGroup outline = scene.BeginMeshOutline(Color.White, 2f);
            foreach (SkinnedMeshHandle mesh in new[] { source, view })
            {
                var draw = new SkinnedInstanceDraw(mesh, Matrix4x4.Identity);
                scene.DrawSkinned(draw, rig.Pose);
                scene.DrawSkinnedOutline(outline, draw, rig.Pose);
            }
        }

        rig.RenderFrame(Submit);   // the first frame allocates the shadow atlases
        IReadOnlyList<RecordingGpuCommandList.IndexedDraw> draws = rig.RenderFrame(Submit);

        Assert.True(rig.Scene.IsSkinnedMeshView(view));
        Assert.False(rig.Scene.IsSkinnedMeshView(source));
        PassDraw[] viewDraws = PassDraws(draws, viewIndices), sourceDraws = PassDraws(draws, sourceIndices);
        Assert.Equal(["cascade 6", "colour 6", "outline 6", "point 6"], Shapes(viewDraws));
        Assert.Equal(["cascade 12", "colour 12", "outline 12", "point 12"], Shapes(sourceDraws));
        // Drawn beside its source under the same settings, the view is drawn exactly as often in every pass.
        Assert.Equal(Counts(sourceDraws), Counts(viewDraws));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_view_follows_its_source_through_a_material_set_replacement(bool displaced)
    {
        using var rig = new ViewRig(gpuSkinning: true);
        Scene3D.TextureHandle albedo = rig.Scene.LoadTexture([255, 255, 255, 255], 1, 1);
        SkinnedMeshHandle source = rig.Scene.LoadSkinnedMesh(Strip(), albedo);
        SkinnedMeshHandle view = displaced
            ? rig.Scene.CreateSkinnedMeshView(source, AlternateTriangles, LiftedPositions())
            : rig.Scene.CreateSkinnedMeshView(source, AlternateTriangles);
        var before = rig.Scene.SkinnedMaterialSetsForTests(source);
        Assert.Equal(before, rig.Scene.SkinnedMaterialSetsForTests(view));

        // A cascade layout change rebuilds every receiver set and destroys the old ones.
        Assert.True(rig.Scene.ReplaceShadowLayout(256, 2));

        var after = rig.Scene.SkinnedMaterialSetsForTests(source);
        Assert.NotSame(before.Skinned, after.Skinned);
        Assert.Equal(after, rig.Scene.SkinnedMaterialSetsForTests(view));
        IReadOnlyList<object> receivers = rig.Scene.LiveMaterialSetIdentitiesForTests;
        Assert.Equal(receivers.Count, receivers.Distinct().Count());
    }

    [Theory]
    [InlineData("empty", false)]
    [InlineData("empty", true)]
    [InlineData("index past the vertex count", false)]
    [InlineData("index past the vertex count", true)]
    [InlineData("count not a multiple of three", false)]
    [InlineData("count not a multiple of three", true)]
    [InlineData("view of a view", false)]
    [InlineData("view of a view", true)]
    [InlineData("source with coverage evidence", false)]
    [InlineData("source with coverage evidence", true)]
    [InlineData("stale source handle", false)]
    [InlineData("stale source handle", true)]
    public void A_bad_view_is_refused(string fault, bool displaced)
    {
        using var coverage = fault == "source with coverage evidence" ? new CoverageBindingFixture() : null;
        using var rig = coverage is null ? new ViewRig(gpuSkinning: true) : null;
        Scene3D scene = coverage?.Scene ?? rig!.Scene;
        uint[] valid = [0, 1, 2];
        SkinnedGltfMesh mesh = coverage?.Meshes[0] ?? Strip();
        SkinnedMeshHandle control = scene.LoadSkinnedMesh(coverage is null ? mesh :
            new SkinnedGltfMesh(mesh.Vertices, mesh.Indices32, mesh.InverseBind, mesh.RestPose, mesh.Skeleton));
        Vector3[] positions = mesh.Vertices.Select(vertex => vertex.Position).ToArray();
        SkinnedMeshHandle Create(SkinnedMeshHandle handle, uint[] list) => displaced
            ? scene.CreateSkinnedMeshView(handle, list, positions)
            : scene.CreateSkinnedMeshView(handle, list);
        SkinnedMeshHandle source = control;
        uint[] indices = valid;
        uint[] controlIndices = valid;
        SkinnedMeshViewRefusal reason;
        switch (fault)
        {
            case "empty": indices = []; reason = SkinnedMeshViewRefusal.IndicesEmpty; break;
            case "index past the vertex count":
                indices = [0, 1, 6];
                controlIndices = [0, 1, 5];
                reason = SkinnedMeshViewRefusal.IndexOutOfRange;
                break;
            case "count not a multiple of three": indices = [0, 1, 2, 3]; reason = SkinnedMeshViewRefusal.IndexCountNotTriangles; break;
            case "view of a view": source = Create(control, valid); reason = SkinnedMeshViewRefusal.SourceIsView; break;
            case "source with coverage evidence": source = coverage!.Hosts[0]; reason = SkinnedMeshViewRefusal.SourceHasCoverage; break;
            case "stale source handle":
                source = scene.LoadSkinnedMesh(Strip());
                scene.UnloadSkinnedMesh(source);
                reason = SkinnedMeshViewRefusal.SourceNotLive;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(fault), fault, null);
        }
        int live = scene.LiveSkinnedMeshCount;

        FakeGpuResourceFactory factory = coverage?.Factory ?? rig!.Factory;
        int buffers = factory.Buffers.Count;
        var refusal = Assert.Throws<SkinnedMeshViewArgumentException>(() => Create(source, indices));

        Assert.Equal(reason, refusal.Reason);
        Assert.Equal(source.Index, refusal.SourceIndex);
        Assert.Equal(source.Generation, refusal.SourceGeneration);
        Assert.Equal(reason == SkinnedMeshViewRefusal.IndexCountNotTriangles ? 4 : null, refusal.IndexCount);
        Assert.Equal(reason == SkinnedMeshViewRefusal.IndexOutOfRange ? 2 : null, refusal.IndexPosition);
        Assert.Equal(reason == SkinnedMeshViewRefusal.IndexOutOfRange ? 6 : null, refusal.VertexCount);
        Assert.Equal(live, scene.LiveSkinnedMeshCount);
        Assert.Empty(factory.Buffers.Skip(buffers));
        // The control differs only in the broken rule: a live, plain, loaded source and in-range indices, the last
        // vertex included.
        Assert.True(scene.IsSkinnedMeshView(Create(control, controlIndices)));
        Assert.Equal(live + 1, scene.LiveSkinnedMeshCount);
    }

    [Fact]
    public void Unloading_a_source_with_a_live_view_throws()
    {
        using var rig = new ViewRig(gpuSkinning: true);
        SkinnedMeshHandle source = rig.LoadStrip(out _);
        SkinnedMeshHandle view = rig.CreateView(source, AlternateTriangles, out FakeBuffer viewIndices);
        int live = rig.Scene.LiveSkinnedMeshCount;

        var refusal = Assert.Throws<SkinnedMeshViewArgumentException>(() => rig.Scene.UnloadSkinnedMesh(source));

        Assert.Equal(SkinnedMeshViewRefusal.SourceHasLiveViews, refusal.Reason);
        Assert.Equal(source.Index, refusal.SourceIndex);
        Assert.Equal(source.Generation, refusal.SourceGeneration);
        Assert.Equal(1, refusal.LiveViews);
        Assert.Equal(live, rig.Scene.LiveSkinnedMeshCount);
        // An unloaded view no longer holds its source, so a despawn can release both in one frame.
        rig.Scene.UnloadSkinnedMesh(view);
        rig.Scene.UnloadSkinnedMesh(source);
        Assert.Equal(live - 2, rig.Scene.LiveSkinnedMeshCount);
        // The source took its unloaded view with it, so the frame boundary has nothing left to free twice, and the
        // view's index buffer still reaches the retire queue.
        rig.Scene.Begin();
        for (int boundary = 0; boundary < GpuRetireQueue.DefaultFrameDelay; boundary++) rig.Scene.Begin();
        Assert.True(viewIndices.Disposed);
    }

    [Fact]
    public void A_view_unloaded_mid_frame_still_draws_this_frame()
    {
        using var rig = new ViewRig(gpuSkinning: true);
        SkinnedMeshHandle source = rig.LoadStrip(out FakeBuffer sourceIndices);
        SkinnedMeshHandle view = rig.CreateView(source, AlternateTriangles, out FakeBuffer viewIndices);
        void Draw(Scene3D scene)
        {
            scene.DrawSkinned(new SkinnedInstanceDraw(source, Matrix4x4.Identity), rig.Pose);
            scene.DrawSkinned(new SkinnedInstanceDraw(view, Matrix4x4.Identity), rig.Pose);
        }
        rig.RenderFrame(Draw);

        IReadOnlyList<RecordingGpuCommandList.IndexedDraw> draws = rig.RenderFrame(scene =>
        {
            Draw(scene);
            scene.UnloadSkinnedMesh(view);
        });

        PassDraw[] viewDraws = PassDraws(draws, viewIndices);
        Assert.Equal(["cascade 6", "colour 6", "point 6"], Shapes(viewDraws));
        Assert.Equal(Counts(PassDraws(draws, sourceIndices)), Counts(viewDraws));
        Assert.False(viewIndices.Disposed);
        Assert.Throws<ArgumentException>(() => rig.Scene.UnloadSkinnedMesh(view));
        // The next frame boundary releases the view and hands its index buffer to the retire queue, which destroys
        // nothing at the boundary that retired it. The fake device has no completion fence, so the queue's frame
        // count decides when it goes.
        rig.Scene.Begin();
        Assert.False(rig.Scene.IsSkinnedMeshView(view));
        Assert.False(viewIndices.Disposed);
        for (int boundary = 0; boundary < GpuRetireQueue.DefaultFrameDelay; boundary++) rig.Scene.Begin();
        Assert.True(viewIndices.Disposed);
    }

    readonly record struct PassDraw(string Pass, uint IndexCount, int Draws);

    // The draws of one index buffer grouped by pass and index count, so a wrong count, a missing pass and a pass that
    // draws the buffer more often than its counterpart each show.
    static PassDraw[] PassDraws(IReadOnlyList<RecordingGpuCommandList.IndexedDraw> draws, FakeBuffer indices) =>
        draws.Where(draw => ReferenceEquals(draw.IndexBuffer, indices))
            .GroupBy(draw => (Pass: PassOf(draw.Pipeline), draw.IndexCount))
            .Select(group => new PassDraw(group.Key.Pass, group.Key.IndexCount, group.Count()))
            .OrderBy(draw => draw.Pass, StringComparer.Ordinal).ThenBy(draw => draw.IndexCount).ToArray();

    static string[] Shapes(PassDraw[] draws) => draws.Select(draw => $"{draw.Pass} {draw.IndexCount}").ToArray();
    static string[] Counts(PassDraw[] draws) => draws.Select(draw => $"{draw.Pass} x{draw.Draws}").ToArray();

    static readonly (string Pass, string[] VertexSources)[] Passes =
    [
        ("colour", [ShaderSources.SkinnedModelVert, ShaderSources.SkinnedModelMotionVert, ShaderSources.ModelVert,
            ShaderSources.ModelMotionVert, ShaderSources.ModelCpuSkinnedMotionVert]),
        ("cascade", [ShaderSources.SkinnedShadowDepthVert, ShaderSources.SkinnedShadowDepthDissolveVert,
            ShaderSources.ShadowDepthVert, ShaderSources.ShadowDepthDissolveVert, ShaderSources.ShadowDepthCutoutVert]),
        ("point", [ShaderSources.PointShadowSkinnedVert, ShaderSources.PointShadowSkinnedDissolveVert,
            ShaderSources.PointShadowRigidVert, ShaderSources.PointShadowRigidDissolveVert]),
        ("outline", [ShaderSources.TargetOutlineSkinnedMaskVert, ShaderSources.TargetOutlineMaskVert]),
    ];

    static string PassOf(IGpuPipeline? pipeline)
    {
        string? vertex = (pipeline as FakePipeline)?.Request?.VertexGlsl;
        foreach ((string pass, string[] sources) in Passes)
            if (Array.IndexOf(sources, vertex) >= 0) return pass;
        return "unknown";
    }

    // Four triangles over a strip of six vertices, all bound to one bone at rest.
    static SkinnedGltfMesh Strip()
    {
        var vertices = new SkinnedVertex[6];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = new SkinnedVertex
            {
                Position = new Vector3(i / 2 * 0.5f, i % 2 * 0.5f, 0f),
                Normal = Vector3.UnitZ,
                Color = Vector4.One,
                BoneWeights = new Vector4(1f, 0f, 0f, 0f),
            };
        uint[] indices = [0, 1, 2, 2, 1, 3, 2, 3, 4, 4, 3, 5];
        return new SkinnedGltfMesh(vertices, indices, [Matrix4x4.Identity], [Matrix4x4.Identity]);
    }

    sealed class ViewRig : IDisposable
    {
        readonly FakeGpuDevice _device = new();
        readonly IGpuTexture _targetTexture;
        readonly IGpuFramebuffer _target;
        internal FakeGpuDevice Device => _device;
        internal FakeGpuResourceFactory Factory => (FakeGpuResourceFactory)_device.Factory;

        internal ViewRig(bool gpuSkinning)
        {
            _targetTexture = Factory.CreateTexture(GpuTextureDescription.Texture2D(
                32, 24, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _target = Factory.CreateFramebuffer(null, _targetTexture);
            Scene = new Scene3D(_device, _target.Outputs, new ShadowSettings { Mode = ShadowMode.ShadowMap })
            { UseGpuSkinning = gpuSkinning, FrustumCulling = false };
            Scene.Post.Starfield = false;
            Scene.Camera.Frame(Vector3.Zero, new Vector3(4f, 3f, 4f));
        }

        internal Scene3D Scene { get; }
        internal Matrix4x4[] Pose { get; } = [Matrix4x4.Identity];

        internal SkinnedMeshHandle LoadStrip(out FakeBuffer indices) => LoadStrip(out indices, out _);

        internal SkinnedMeshHandle LoadStrip(out FakeBuffer indices, out FakeBuffer vertices)
        {
            int before = Factory.Buffers.Count;
            SkinnedMeshHandle handle = Scene.LoadSkinnedMesh(Strip());
            indices = NewIndexBuffer(before);
            vertices = NewVertexBuffer(before);
            return handle;
        }

        internal SkinnedMeshHandle CreateView(SkinnedMeshHandle source, uint[] triangles, out FakeBuffer indices)
        {
            int before = Factory.Buffers.Count;
            SkinnedMeshHandle view = Scene.CreateSkinnedMeshView(source, triangles);
            indices = NewIndexBuffer(before);
            return view;
        }

        internal SkinnedMeshHandle CreateDisplacedView(SkinnedMeshHandle source, Vector3[] positions,
            out FakeBuffer indices, out FakeBuffer vertices)
        {
            int before = Factory.Buffers.Count;
            SkinnedMeshHandle view = Scene.CreateSkinnedMeshView(source, AlternateTriangles, positions);
            indices = NewIndexBuffer(before);
            vertices = NewVertexBuffer(before);
            return view;
        }

        FakeBuffer NewIndexBuffer(int before) =>
            Assert.Single(Factory.Buffers.Skip(before), buffer => buffer.Usage.HasFlag(GpuBufferUsage.IndexBuffer));

        FakeBuffer NewVertexBuffer(int before) =>
            Assert.Single(Factory.Buffers.Skip(before), buffer => buffer.Usage.HasFlag(GpuBufferUsage.VertexBuffer));

        internal IReadOnlyList<RecordingGpuCommandList.IndexedDraw> RenderFrame(Action<Scene3D> submit)
        {
            using RecordingGpuCommandList recording = RecordFrame(submit);
            return recording.IndexedDraws.ToArray();
        }

        internal RecordingGpuCommandList RecordFrame(Action<Scene3D> submit, bool capturePayloads = false)
        {
            Scene.Begin();
            Scene.AddLight(new Vector3(0f, 1f, 1f), Color.White, 10f, 1f, LightShadow.Dynamic);
            submit(Scene);
            var recording = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = capturePayloads };
            Scene.PrepareFrame();
            recording.Begin();
            Scene.RenderInternal(recording, 32, 24, _target);
            recording.End();
            return recording;
        }

        public void Dispose()
        {
            Scene.Dispose();
            _target.Dispose();
            _targetTexture.Dispose();
            _device.Dispose();
        }
    }
}
