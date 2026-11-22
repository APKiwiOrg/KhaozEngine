using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed partial class SkinnedMeshViewTests
{
    static Vector3[] LiftedPositions() =>
        Strip().Vertices.Select(vertex => vertex.Position + Vector3.UnitZ * 0.25f).ToArray();

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_displaced_view_binds_its_own_vertices_in_every_gpu_skinned_pass(bool temporal, bool dissolve)
    {
        using var rig = new ViewRig(gpuSkinning: true);
        rig.Scene.ForceTemporalForTests = temporal;
        SkinnedMeshHandle source = rig.LoadStrip(out _, out FakeBuffer sourceVertices);
        SkinnedMeshHandle plain = rig.CreateView(source, AlternateTriangles, out FakeBuffer plainIndices);
        SkinnedMeshHandle view = rig.CreateDisplacedView(source, LiftedPositions(), out FakeBuffer viewIndices,
            out FakeBuffer viewVertices);
        void Submit(Scene3D scene)
        {
            MeshOutlineGroup outline = scene.BeginMeshOutline(Color.White, 2f);
            foreach (SkinnedMeshHandle handle in new[] { source, plain, view })
            {
                var draw = new SkinnedInstanceDraw(handle, Matrix4x4.Identity)
                {
                    Dissolve = dissolve ? 0.5f : 0f,
                    Motion = MotionKey.From((ulong)handle.Index + 1),
                };
                scene.DrawSkinned(draw, rig.Pose);
                scene.DrawSkinnedOutline(outline, draw, rig.Pose);
            }
        }

        rig.RenderFrame(Submit);
        var draws = rig.RenderFrame(Submit);

        Assert.True(rig.Scene.IsSkinnedMeshView(view));
        Assert.Equal(["cascade 6", "colour 6", "outline 6", "point 6"], Shapes(PassDraws(draws, viewIndices)));
        Assert.Equal(["cascade 6", "colour 6", "outline 6", "point 6"], Shapes(PassDraws(draws, plainIndices)));
        var colour = Assert.Single(draws, draw => draw.IndexBuffer == viewIndices && PassOf(draw.Pipeline) == "colour");
        var pipeline = Assert.IsType<FakePipeline>(colour.Pipeline);
        var colourShaders = Assert.IsType<FakeGraphicsPipelineRequest>(pipeline.Request);
        Assert.Equal(temporal ? ShaderSources.SkinnedModelMotionVert : ShaderSources.SkinnedModelVert, colourShaders.VertexGlsl);
        string expectedFragment = (temporal, dissolve) switch
        {
            (true, true) => ShaderSources.SkinnedModelDissolveMotionFrag,
            (true, false) => ShaderSources.SkinnedModelMotionFrag,
            (false, true) => ShaderSources.SkinnedModelDissolveFrag,
            _ => ShaderSources.SkinnedModelFrag,
        };
        if (ShadowVisibilityTargets.HasVisibility(colourShaders.Description.Outputs))
            expectedFragment = ShadowVisibilityTargets.Fragment(expectedFragment, motion: temporal);
        Assert.Equal(expectedFragment, colourShaders.FragmentGlsl);
        Assert.Equal((uint)(AlternateTriangles.Length * sizeof(ushort)), viewIndices.SizeInBytes);
        Assert.All(draws.Where(draw => draw.IndexBuffer == viewIndices),
            draw => Assert.Same(viewVertices, draw.VertexBuffer));
        Assert.All(draws.Where(draw => draw.IndexBuffer == plainIndices),
            draw => Assert.Same(sourceVertices, draw.VertexBuffer));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_displaced_view_refuses_invalid_rest_positions(bool wrongCount)
    {
        using var rig = new ViewRig(gpuSkinning: true);
        SkinnedMeshHandle source = rig.LoadStrip(out _);
        Vector3[] valid = LiftedPositions();
        Vector3[] invalid = wrongCount ? valid[..^1] : (Vector3[])valid.Clone();
        const int nonFiniteVertex = 2;
        if (!wrongCount) invalid[nonFiniteVertex].Z = float.NaN;
        int live = rig.Scene.LiveSkinnedMeshCount;
        int buffers = rig.Factory.Buffers.Count;

        var refusal = Assert.Throws<SkinnedMeshViewArgumentException>(
            () => rig.Scene.CreateSkinnedMeshView(source, AlternateTriangles, invalid));

        Assert.Equal(wrongCount ? SkinnedMeshViewRefusal.PositionCountMismatch : SkinnedMeshViewRefusal.PositionNonFinite,
            refusal.Reason);
        Assert.Equal(source.Index, refusal.SourceIndex);
        Assert.Equal(source.Generation, refusal.SourceGeneration);
        Assert.Equal(wrongCount ? valid.Length : null, refusal.VertexCount);
        Assert.Equal(wrongCount ? invalid.Length : null, refusal.PositionCount);
        Assert.Equal(wrongCount ? null : nonFiniteVertex, refusal.VertexIndex);
        Assert.Equal(live, rig.Scene.LiveSkinnedMeshCount);
        Assert.Empty(rig.Factory.Buffers.Skip(buffers));
        Assert.True(rig.Scene.IsSkinnedMeshView(rig.Scene.CreateSkinnedMeshView(source, AlternateTriangles, valid)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_displaced_view_skins_its_rest_positions_on_the_cpu(bool temporal)
    {
        using var rig = new ViewRig(gpuSkinning: false);
        rig.Scene.ForceTemporalForTests = temporal;
        rig.Scene.RenderOrigin = Vector3.Zero;
        SkinnedMeshHandle source = rig.LoadStrip(out _);
        Vector3[] positions = LiftedPositions();
        SkinnedMeshHandle view = rig.CreateDisplacedView(source, positions, out FakeBuffer indices, out _);
        Vector3[] expected = (Vector3[])positions.Clone();
        Array.Fill(positions, Vector3.Zero);
        var key = MotionKey.From(1);

        void Submit(Scene3D scene, Matrix4x4 world)
        {
            var draw = new SkinnedInstanceDraw(view, world) { Motion = key };
            scene.DrawSkinned(draw, rig.Pose);
            MeshOutlineGroup outline = scene.BeginMeshOutline(Color.White, 2f, MeshOutlineOcclusion.None);
            scene.DrawSkinnedOutline(outline, draw, rig.Pose);
        }

        rig.RenderFrame(scene => Submit(scene, Matrix4x4.Identity));
        // The new world transform makes reusing the current position for the previous stream observably wrong.
        using RecordingGpuCommandList frame = rig.RecordFrame(
            scene => Submit(scene, Matrix4x4.CreateTranslation(Vector3.UnitX)), capturePayloads: true);

        var colour = Assert.Single(frame.IndexedDraws, draw => draw.IndexBuffer == indices && PassOf(draw.Pipeline) == "colour");
        var outline = Assert.Single(frame.IndexedDraws, draw => draw.IndexBuffer == indices && PassOf(draw.Pipeline) == "outline");
        Assert.Equal(expected, UploadedModelPositions(frame, colour.VertexBuffer));
        Assert.Equal(expected, UploadedModelPositions(frame, outline.VertexBuffer));
        if (temporal)
        {
            Assert.Equal(ShaderSources.ModelCpuSkinnedMotionVert, Assert.IsType<FakePipeline>(colour.Pipeline).Request?.VertexGlsl);
            IGpuBuffer previous = rig.Scene.MotionResourcesForTests!.CpuPrevious;
            Assert.Equal(expected, MemoryMarshal.Cast<byte, Vector3>(UploadedData(frame, previous)).ToArray());
        }
    }

    static Vector3[] UploadedModelPositions(RecordingGpuCommandList frame, IGpuBuffer? buffer) =>
        MemoryMarshal.Cast<byte, ModelVertex>(UploadedData(frame, buffer)).ToArray().Select(vertex => vertex.Position).ToArray();

    static byte[] UploadedData(RecordingGpuCommandList frame, IGpuBuffer? buffer)
    {
        Assert.NotNull(buffer);
        var upload = Assert.Single(frame.Uploads, upload => upload.Buffer == buffer);
        return Assert.IsType<byte[]>(upload.Data);
    }

    [Fact]
    public void A_displaced_view_keeps_its_largest_displacement_when_source_bounds_grow()
    {
        using var rig = new ViewRig(gpuSkinning: true);
        Scene3D scene = rig.Scene;
        SkinnedMeshHandle source = rig.LoadStrip(out _);
        SkinnedMeshHandle plain = rig.CreateView(source, AlternateTriangles, out _);
        Vector3[] positions = LiftedPositions();
        positions[^1] = Strip().Vertices[^1].Position + new Vector3(3f / 16f, 0f, 4f / 16f);
        SkinnedMeshHandle view = rig.CreateDisplacedView(source, positions, out _, out _);
        var margin = new Vector3(5f / 16f);

        void CheckBounds()
        {
            MeshBounds sourceBounds = scene.SkinnedBoundsAt(source);
            MeshBounds displacedBounds = scene.SkinnedBoundsAt(view);
            Assert.Equal(sourceBounds.Min - margin, displacedBounds.Min);
            Assert.Equal(sourceBounds.Max + margin, displacedBounds.Max);
            Assert.Equal(sourceBounds, scene.SkinnedBoundsAt(plain));
        }

        CheckBounds();
        Assert.True(scene.ReplaceShadowLayout(256, 2));
        scene.GrowSkinnedBounds(source, new MeshBounds(new Vector3(-2f, -3f, -4f), new Vector3(3f, 4f, 5f)));
        CheckBounds();
    }

    [Fact]
    public void An_unloaded_displaced_view_draws_this_frame_and_retires_both_buffers_at_the_boundary()
    {
        using var rig = new ViewRig(gpuSkinning: true);
        rig.Factory.StrictBufferDisposal = true;
        SkinnedMeshHandle source = rig.LoadStrip(out FakeBuffer sourceIndices, out FakeBuffer sourceVertices);
        SkinnedMeshHandle view = rig.CreateDisplacedView(source, LiftedPositions(), out FakeBuffer viewIndices,
            out FakeBuffer viewVertices);
        var draws = rig.RenderFrame(scene =>
        {
            scene.DrawSkinned(new SkinnedInstanceDraw(view, Matrix4x4.Identity), rig.Pose);
            scene.UnloadSkinnedMesh(view);
        });

        Assert.Contains(draws, draw => draw.IndexBuffer == viewIndices && draw.VertexBuffer == viewVertices);
        Assert.True(rig.Scene.IsSkinnedMeshView(view));
        Assert.False(viewIndices.Disposed);
        Assert.False(viewVertices.Disposed);
        rig.Scene.Begin();
        Assert.False(rig.Scene.IsSkinnedMeshView(view));
        Assert.False(viewIndices.Disposed);
        Assert.False(viewVertices.Disposed);
        for (int boundary = 0; boundary < GpuRetireQueue.DefaultFrameDelay; boundary++) rig.Scene.Begin();
        Assert.True(viewIndices.Disposed);
        Assert.True(viewVertices.Disposed);
        Assert.False(sourceIndices.Disposed);
        Assert.False(sourceVertices.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_displaced_view_and_its_source_each_release_their_own_buffers_once(bool unload)
    {
        using var rig = new ViewRig(gpuSkinning: true);
        rig.Factory.StrictBufferDisposal = true;
        SkinnedMeshHandle source = rig.LoadStrip(out FakeBuffer sourceIndices, out FakeBuffer sourceVertices);
        SkinnedMeshHandle view = rig.CreateDisplacedView(source, LiftedPositions(), out FakeBuffer viewIndices,
            out FakeBuffer viewVertices);

        if (unload)
        {
            var refusal = Assert.Throws<SkinnedMeshViewArgumentException>(() => rig.Scene.UnloadSkinnedMesh(source));
            Assert.Equal(SkinnedMeshViewRefusal.SourceHasLiveViews, refusal.Reason);
            Assert.Equal(source.Index, refusal.SourceIndex);
            Assert.Equal(source.Generation, refusal.SourceGeneration);
            Assert.Equal(1, refusal.LiveViews);
            rig.Scene.UnloadSkinnedMesh(view);
            rig.Scene.UnloadSkinnedMesh(source);
            Assert.False(rig.Scene.IsSkinnedMeshView(view));
            Assert.Equal(0, rig.Scene.LiveSkinnedMeshCount);
            Assert.False(viewVertices.Disposed);
            Assert.False(viewIndices.Disposed);
            Assert.False(sourceVertices.Disposed);
            Assert.False(sourceIndices.Disposed);
            rig.Scene.Begin();
            for (int boundary = 0; boundary < GpuRetireQueue.DefaultFrameDelay; boundary++) rig.Scene.Begin();
            Assert.True(viewVertices.Disposed);
            Assert.True(viewIndices.Disposed);
            Assert.True(sourceVertices.Disposed);
            Assert.True(sourceIndices.Disposed);
        }
        else
        {
            Assert.False(viewVertices.Disposed);
            Assert.False(viewIndices.Disposed);
            Assert.False(sourceVertices.Disposed);
            Assert.False(sourceIndices.Disposed);
        }

        rig.Scene.Dispose();
        Assert.True(sourceIndices.Disposed);
        Assert.True(sourceVertices.Disposed);
        Assert.True(viewIndices.Disposed);
        Assert.True(viewVertices.Disposed);
    }

    [Fact]
    public void A_failed_displaced_view_upload_retires_the_unpublished_buffers()
    {
        using var rig = new ViewRig(gpuSkinning: true);
        rig.Factory.StrictBufferDisposal = true;
        SkinnedMeshHandle source = rig.LoadStrip(out FakeBuffer sourceIndices, out FakeBuffer sourceVertices);
        Vector3[] positions = LiftedPositions();
        int buffers = rig.Factory.Buffers.Count;
        rig.Device.OnBufferUpdate = buffer =>
        {
            if (buffer is FakeBuffer { Usage: GpuBufferUsage.IndexBuffer })
                throw new InvalidOperationException("Synthetic index upload fault.");
        };

        Assert.Throws<InvalidOperationException>(() => rig.Scene.CreateSkinnedMeshView(source, AlternateTriangles, positions));

        rig.Device.OnBufferUpdate = null;
        Assert.Equal(1, rig.Scene.LiveSkinnedMeshCount);
        FakeBuffer[] unpublished = rig.Factory.Buffers.Skip(buffers).ToArray();
        Assert.Equal(2, unpublished.Length);
        Assert.All(unpublished, buffer => Assert.False(buffer.Disposed));
        rig.Scene.Begin();
        for (int boundary = 0; boundary < GpuRetireQueue.DefaultFrameDelay; boundary++) rig.Scene.Begin();
        Assert.All(unpublished, buffer => Assert.True(buffer.Disposed));
        Assert.False(sourceIndices.Disposed);
        Assert.False(sourceVertices.Disposed);
        Assert.True(rig.Scene.IsSkinnedMeshView(rig.Scene.CreateSkinnedMeshView(source, AlternateTriangles, positions)));
    }
}
