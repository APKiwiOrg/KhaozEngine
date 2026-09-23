using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SkinnedTargetOutlineRendererTests
{
    [Fact]
    public void Gpu_outline_records_with_zero_ordinary_skinned_instances()
    {
        using var h = new Harness();
        SkinnedGltfMesh tube = Tube();
        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(tube);
        FrameRecord frame = h.Record(scene =>
        {
            MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1.25f);
            scene.DrawSkinnedOutline(group, mesh, tube.RestPose, Matrix4x4.Identity);
            Assert.Equal(0, scene.SkinnedInstanceCount);
        });

        Assert.Equal(0, h.Scene.SkinnedInstanceCount);
        Assert.Equal(2, frame.OutlineMaskDraws.Count);
        Assert.Equal(1, frame.Delta(GpuCommandKind.Draw));
    }

    [Fact]
    public void Cpu_outline_records_with_zero_ordinary_skinned_instances()
    {
        using var h = new Harness();
        SkinnedGltfMesh tube = Tube();
        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(tube);
        h.Scene.UseGpuSkinning = false;

        FrameRecord frame = h.Record(scene =>
        {
            MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1.25f);
            scene.DrawSkinnedOutline(group, mesh, tube.RestPose, Matrix4x4.Identity);
            Assert.Equal(0, scene.SkinnedInstanceCount);
        });

        Assert.Equal(0, h.Scene.SkinnedInstanceCount);
        Assert.Equal(2, frame.OutlineMaskDraws.Count);
        Assert.All(frame.OutlineMaskDraws, draw => Assert.True(IsRigidMask(draw)));
        Assert.Single(CpuVertexUploads(frame, tube.Vertices.Length));
    }

    [Fact]
    public void Cpu_parts_share_one_vertex_upload_and_advance_base_vertex_offsets()
    {
        using var h = new Harness();
        SkinnedGltfMesh tube = Tube();
        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(tube);
        h.Scene.UseGpuSkinning = false;

        FrameRecord frame = h.Record(scene =>
        {
            MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1.25f);
            scene.DrawSkinnedOutline(group, mesh, tube.RestPose, Matrix4x4.Identity);
            scene.DrawSkinnedOutline(group, mesh, tube.RestPose, Matrix4x4.CreateTranslation(1f, 0f, 0f));
        });

        int vertexCount = tube.Vertices.Length;
        Assert.Equal(new[] { 0, vertexCount, 0, vertexCount },
            frame.OutlineMaskDraws.Select(draw => draw.VertexOffset).ToArray());
        Assert.Single(CpuVertexUploads(frame, vertexCount * 2));
        Assert.Single(frame.OutlineMaskDraws.Select(draw => draw.VertexBuffer).Distinct());
    }

    [Fact]
    public void Cpu_and_gpu_paths_read_the_same_outline_pose_snapshot()
    {
        SkinnedGltfMesh tube = Tube();
        Matrix4x4[] pose = BentPose(tube, 0.42f);
        using var gpu = new Harness();
        SkinnedMeshHandle gpuMesh = gpu.Scene.LoadSkinnedMesh(tube);
        FrameRecord gpuFrame = gpu.Record(scene =>
        {
            MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1.25f);
            scene.DrawSkinnedOutline(group, gpuMesh, pose, Matrix4x4.Identity);
        });
        RecordingGpuCommandList.Upload paletteUpload = Assert.Single(gpuFrame.Uploads,
            upload => upload.Bytes == TargetOutlineSkinningStore.PaletteSlotBytes);

        using var cpu = new Harness();
        SkinnedMeshHandle cpuMesh = cpu.Scene.LoadSkinnedMesh(tube);
        FrameRecord cpuFrame = cpu.Record(scene =>
        {
            MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1.25f);
            scene.DrawSkinnedOutline(group, cpuMesh, pose, Matrix4x4.Identity);
            scene.UseGpuSkinning = false;
        });
        RecordingGpuCommandList.Upload cpuUpload = Assert.Single(CpuVertexUploads(cpuFrame, tube.Vertices.Length));

        ReadOnlySpan<Matrix4x4> palette = MemoryMarshal.Cast<byte, Matrix4x4>(paletteUpload.Data!)
            .Slice(0, tube.BoneCount);
        ModelVertex[] expected = Skin(tube.Vertices, palette);
        ModelVertex[] actual = MemoryMarshal.Cast<byte, ModelVertex>(cpuUpload.Data!).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Outline_pose_is_separate_from_the_ordinary_pose_for_the_same_handle()
    {
        using var h = new Harness();
        SkinnedGltfMesh tube = Tube();
        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(tube);
        Matrix4x4[] ordinaryPose = (Matrix4x4[])tube.RestPose.Clone();
        Matrix4x4[] outlinePose = BentPose(tube, 0.55f);
        h.Scene.UseGpuSkinning = false;

        FrameRecord frame = h.Record(scene =>
        {
            scene.DrawSkinned(mesh, ordinaryPose, Matrix4x4.Identity, Color.White);
            MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1.25f);
            scene.DrawSkinnedOutline(group, mesh, outlinePose, Matrix4x4.Identity);
        });

        RecordingGpuCommandList.Upload[] uploads = CpuVertexUploads(frame, tube.Vertices.Length);
        Assert.Equal(2, uploads.Length);
        ModelVertex[] ordinary = MemoryMarshal.Cast<byte, ModelVertex>(uploads[0].Data!).ToArray();
        ModelVertex[] outline = MemoryMarshal.Cast<byte, ModelVertex>(uploads[1].Data!).ToArray();
        Assert.Equal(Skin(tube.Vertices, Compose(tube, ordinaryPose)), ordinary);
        Assert.Equal(Skin(tube.Vertices, Compose(tube, outlinePose)), outline);
        Assert.False(ordinary.AsSpan().SequenceEqual(outline));
    }

    [Fact]
    public void Gpu_palette_slots_advance_by_TargetOutlineSkinningStore_PaletteSlotBytes()
    {
        using var h = new Harness();
        SkinnedGltfMesh tube = Tube();
        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(tube);
        FrameRecord frame = h.Record(scene =>
        {
            MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1.25f);
            scene.DrawSkinnedOutline(group, mesh, tube.RestPose, Matrix4x4.Identity);
            scene.DrawSkinnedOutline(group, mesh, tube.RestPose, Matrix4x4.CreateTranslation(1f, 0f, 0f));
        });

        uint slot = TargetOutlineSkinningStore.PaletteSlotBytes;
        Assert.Equal(new uint[] { 0, slot, 0, slot }, frame.SkinnedPaletteOffsets);
    }

    [Fact]
    public void Two_groups_bind_and_upload_disjoint_palette_slots_before_submission()
    {
        using var h = new Harness();
        SkinnedGltfMesh tube = Tube();
        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(tube);
        Matrix4x4[] bent = (Matrix4x4[])tube.RestPose.Clone();
        bent[1] = Matrix4x4.CreateRotationX(0.35f) * bent[1];
        FrameRecord frame = h.Record(scene =>
        {
            MeshOutlineGroup first = scene.BeginMeshOutline(Color.White, 1.25f);
            scene.DrawSkinnedOutline(first, mesh, tube.RestPose, Matrix4x4.Identity);
            MeshOutlineGroup second = scene.BeginMeshOutline(new Color(1f, 0f, 0f, 1f), 1.25f);
            scene.DrawSkinnedOutline(second, mesh, bent, Matrix4x4.Identity);
        });

        uint slot = TargetOutlineSkinningStore.PaletteSlotBytes;
        Assert.Equal(new uint[] { 0, 0, slot, slot }, frame.SkinnedPaletteOffsets);
        RecordingGpuCommandList.Upload[] paletteUploads = frame.Uploads
            .Where(upload => upload.Bytes == slot)
            .ToArray();
        Assert.Collection(paletteUploads,
            first => Assert.Equal((0u, slot), (first.Offset, first.Bytes)),
            second => Assert.Equal((slot, slot), (second.Offset, second.Bytes)));
        ReadOnlySpan<Matrix4x4> firstPalette = MemoryMarshal.Cast<byte, Matrix4x4>(paletteUploads[0].Data!);
        ReadOnlySpan<Matrix4x4> secondPalette = MemoryMarshal.Cast<byte, Matrix4x4>(paletteUploads[1].Data!);
        Assert.NotEqual(firstPalette[1], secondPalette[1]);
        IGpuBuffer paletteBuffer = paletteUploads[0].Buffer;
        RecordingGpuCommandList.BoundRead[] paletteReads = frame.Reads
            .Where(read => ReferenceEquals(read.Buffer, paletteBuffer))
            .ToArray();
        Assert.NotEmpty(paletteReads);
        Assert.Empty(UniformRewriteAudit.Scan(paletteUploads, h.IsUniform, paletteReads));
    }

    [Fact]
    public void Mixed_rigid_and_gpu_skinned_parts_keep_submission_order_in_both_masks()
    {
        using var h = new Harness();
        MeshHandle rigid = h.Scene.LoadMesh(MeshPrimitives.Box(1f));
        SkinnedGltfMesh tube = Tube();
        SkinnedMeshHandle skinned = h.Scene.LoadSkinnedMesh(tube);
        FrameRecord frame = h.Record(scene =>
        {
            MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1.25f);
            scene.DrawMeshOutline(group, rigid, Matrix4x4.Identity);
            scene.DrawSkinnedOutline(group, skinned, tube.RestPose, Matrix4x4.Identity);
            scene.DrawMeshOutline(group, rigid, Matrix4x4.CreateTranslation(1f, 0f, 0f));
        });

        Assert.Equal(6, frame.OutlineMaskDraws.Count);
        Assert.Same(frame.OutlineMaskDraws[0].VertexBuffer, frame.OutlineMaskDraws[2].VertexBuffer);
        Assert.Same(frame.OutlineMaskDraws[0].VertexBuffer, frame.OutlineMaskDraws[3].VertexBuffer);
        Assert.Same(frame.OutlineMaskDraws[0].VertexBuffer, frame.OutlineMaskDraws[5].VertexBuffer);
        Assert.Same(frame.OutlineMaskDraws[1].VertexBuffer, frame.OutlineMaskDraws[4].VertexBuffer);
        Assert.NotSame(frame.OutlineMaskDraws[0].VertexBuffer, frame.OutlineMaskDraws[1].VertexBuffer);
        Assert.True(IsRigidFull(frame.OutlineMaskDraws[0]));
        Assert.True(IsSkinnedFull(frame.OutlineMaskDraws[1]));
        Assert.True(IsRigidFull(frame.OutlineMaskDraws[2]));
        Assert.True(IsRigidVisible(frame.OutlineMaskDraws[3]));
        Assert.True(IsSkinnedVisible(frame.OutlineMaskDraws[4]));
        Assert.True(IsRigidVisible(frame.OutlineMaskDraws[5]));
    }

    [Fact]
    public void Mixed_group_records_one_full_pass_one_visible_pass_and_one_composite()
    {
        using var h = new Harness();
        MeshHandle rigid = h.Scene.LoadMesh(MeshPrimitives.Box(1f));
        SkinnedGltfMesh tube = Tube();
        SkinnedMeshHandle skinned = h.Scene.LoadSkinnedMesh(tube);
        FrameRecord frame = h.Record(scene =>
        {
            MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1.25f);
            scene.DrawMeshOutline(group, rigid, Matrix4x4.Identity);
            scene.DrawSkinnedOutline(group, skinned, tube.RestPose, Matrix4x4.Identity);
        });

        Assert.Equal(3, frame.Delta(GpuCommandKind.SetFramebuffer));
        Assert.Equal(3, frame.Delta(GpuCommandKind.ClearColorTarget));
        Assert.Equal(1, frame.Delta(GpuCommandKind.ClearDepthStencil));
        Assert.Equal(4, frame.Delta(GpuCommandKind.DrawIndexed));
        Assert.Equal(1, frame.Delta(GpuCommandKind.Draw));
        Assert.Equal(5, frame.DrawCalls - frame.BaselineDrawCalls);
    }

    [Fact]
    public void Fully_invisible_endpoints_record_no_outline_work()
    {
        using var h = new Harness();
        SkinnedGltfMesh tube = Tube();
        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(tube);
        FrameRecord frame = h.Record(scene =>
        {
            MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1.25f);
            scene.DrawSkinnedOutlineDissolved(group, mesh, tube.RestPose, Matrix4x4.Identity, 1f, false);
            scene.DrawSkinnedOutlineDissolved(group, mesh, tube.RestPose, Matrix4x4.Identity, 0f, true);
        });

        AssertNoOutlineWork(frame);
    }

    [Fact]
    public void Unloaded_after_submission_records_no_mask_draw()
    {
        using var h = new Harness();
        SkinnedGltfMesh tube = Tube();
        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(tube);
        FrameRecord frame = h.Record(scene =>
        {
            MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1.25f);
            scene.DrawSkinnedOutline(group, mesh, tube.RestPose, Matrix4x4.Identity);
            scene.UnloadSkinnedMesh(mesh);
        });

        Assert.Empty(frame.OutlineMaskDraws);
        Assert.Equal(0, frame.Delta(GpuCommandKind.Draw));
    }

    [Fact]
    public void Scene_depth_group_with_no_live_parts_records_no_composite()
    {
        using var h = new Harness();
        FrameRecord frame = h.Record(scene =>
        {
            MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1.25f);
            scene.DrawMeshOutline(group, default, Matrix4x4.Identity);
        });

        AssertNoOutlineWork(frame);
    }

    [Fact]
    public void Rigid_only_group_allocates_and_uploads_no_skinning_buffer()
    {
        using var h = new Harness();
        MeshHandle rigid = h.Scene.LoadMesh(MeshPrimitives.Box(1f));
        FrameRecord frame = h.Record(scene =>
        {
            MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1.25f);
            scene.DrawMeshOutline(group, rigid, Matrix4x4.Identity);
        });

        Assert.DoesNotContain(frame.NewBuffers,
            buffer => buffer.SizeInBytes >= TargetOutlineSkinningStore.PaletteSlotBytes);
        Assert.DoesNotContain(frame.Uploads, upload =>
            upload.Buffer.SizeInBytes >= TargetOutlineSkinningStore.PaletteSlotBytes
            && upload.Bytes >= TargetOutlineSkinningStore.PaletteSlotBytes);
        Assert.Equal(2, frame.OutlineMaskDraws.Count);
    }

    [Fact]
    public void Gpu_palette_growth_failure_keeps_the_old_capacity_and_aborts_before_composite()
    {
        using var h = new Harness();
        SkinnedGltfMesh tube = Tube();
        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(tube);
        MeshHandle rigid = h.Scene.LoadMesh(MeshPrimitives.Box(1f));
        WarmRigidCapacity(h, rigid, 5);
        FrameRecord small = h.Record(scene => SubmitSkinnedParts(scene, mesh, tube.RestPose, 1));
        FakeBuffer oldBuffer = Assert.Single(small.NewBuffers,
            buffer => buffer.SizeInBytes == 4 * TargetOutlineSkinningStore.PaletteSlotBytes);
        FakeResourceSet oldSet = FindSet(h.Factory, oldBuffer);
        h.Factory.ThrowOnResourceSetCreate = h.Factory.ResourceSets.Count + 1;

        FrameRecord failed = h.RecordFailure(scene => SubmitSkinnedParts(scene, mesh, tube.RestPose, 5));

        Assert.IsType<InvalidOperationException>(failed.Failure);
        AssertFailedBeforeMask(failed);
        Assert.False(oldBuffer.Disposed);
        Assert.False(oldSet.Disposed);
        Assert.True(failed.NewBuffers[^1].Disposed);
        h.Factory.ThrowOnResourceSetCreate = 0;
        Assert.Equal(2, h.Record(scene => SubmitSkinnedParts(scene, mesh, tube.RestPose, 1)).OutlineMaskDraws.Count);
        h.Record(scene => SubmitSkinnedParts(scene, mesh, tube.RestPose, 5));
        Assert.False(oldBuffer.Disposed);
        Assert.False(oldSet.Disposed);
    }

    [Fact]
    public void Cpu_vertex_growth_failure_keeps_the_old_capacity_and_aborts_before_composite()
    {
        using var h = new Harness();
        SkinnedGltfMesh tube = Tube();
        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(tube);
        MeshHandle rigid = h.Scene.LoadMesh(MeshPrimitives.Box(1f));
        int growthParts = 256 / tube.Vertices.Length + 1;
        WarmRigidCapacity(h, rigid, growthParts);
        h.Scene.UseGpuSkinning = false;
        FrameRecord small = h.Record(scene => SubmitSkinnedParts(scene, mesh, tube.RestPose, 1));
        FakeBuffer oldBuffer = Assert.Single(small.NewBuffers,
            buffer => buffer.SizeInBytes == 256 * ModelVertex.SizeInBytes);
        h.Factory.ThrowOnBufferCreate = h.Factory.Buffers.Count + 1;

        FrameRecord failed = h.RecordFailure(
            scene => SubmitSkinnedParts(scene, mesh, tube.RestPose, growthParts));

        Assert.IsType<InvalidOperationException>(failed.Failure);
        AssertFailedBeforeMask(failed);
        Assert.False(oldBuffer.Disposed);
        h.Factory.ThrowOnBufferCreate = 0;
        Assert.Equal(2, h.Record(scene => SubmitSkinnedParts(scene, mesh, tube.RestPose, 1)).OutlineMaskDraws.Count);
        h.Record(scene => SubmitSkinnedParts(scene, mesh, tube.RestPose, growthParts));
        Assert.False(oldBuffer.Disposed);
    }

    [Fact]
    public void Grown_resources_stay_alive_until_renderer_disposal()
    {
        var h = new Harness();
        SkinnedGltfMesh tube = Tube();
        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(tube);
        MeshHandle rigid = h.Scene.LoadMesh(MeshPrimitives.Box(1f));
        int cpuGrowthParts = 256 / tube.Vertices.Length + 1;
        WarmRigidCapacity(h, rigid, Math.Max(5, cpuGrowthParts));
        FrameRecord gpuSmall = h.Record(scene => SubmitSkinnedParts(scene, mesh, tube.RestPose, 1));
        FakeBuffer oldPalette = Assert.Single(gpuSmall.NewBuffers,
            buffer => buffer.SizeInBytes == 4 * TargetOutlineSkinningStore.PaletteSlotBytes);
        FakeResourceSet oldPaletteSet = FindSet(h.Factory, oldPalette);
        h.Scene.UseGpuSkinning = false;
        FrameRecord cpuSmall = h.Record(scene => SubmitSkinnedParts(scene, mesh, tube.RestPose, 1));
        FakeBuffer oldCpu = Assert.Single(cpuSmall.NewBuffers,
            buffer => buffer.SizeInBytes == 256 * ModelVertex.SizeInBytes);

        h.Scene.UseGpuSkinning = true;
        h.Record(scene => SubmitSkinnedParts(scene, mesh, tube.RestPose, 5));
        h.Scene.UseGpuSkinning = false;
        h.Record(scene => SubmitSkinnedParts(scene, mesh, tube.RestPose, cpuGrowthParts));

        Assert.False(oldPalette.Disposed);
        Assert.False(oldPaletteSet.Disposed);
        Assert.False(oldCpu.Disposed);
        h.Dispose();
        Assert.True(oldPalette.Disposed);
        Assert.True(oldPaletteSet.Disposed);
        Assert.True(oldCpu.Disposed);
    }

    static void AssertNoOutlineWork(FrameRecord frame)
    {
        foreach (GpuCommandKind kind in new[]
        {
            GpuCommandKind.UpdateBuffer,
            GpuCommandKind.DrawIndexed,
            GpuCommandKind.ClearColorTarget,
            GpuCommandKind.ClearDepthStencil,
            GpuCommandKind.SetFramebuffer,
            GpuCommandKind.Draw,
        })
            Assert.Equal(0, frame.Delta(kind));
        Assert.Equal(frame.BaselineDrawCalls, frame.DrawCalls);
    }

    static void AssertFailedBeforeMask(FrameRecord frame)
    {
        Assert.Empty(frame.OutlineMaskDraws);
        foreach (GpuCommandKind kind in new[]
        {
            GpuCommandKind.ClearColorTarget,
            GpuCommandKind.ClearDepthStencil,
            GpuCommandKind.SetFramebuffer,
            GpuCommandKind.DrawIndexed,
            GpuCommandKind.Draw,
        })
            Assert.Equal(0, frame.Delta(kind));
    }

    static bool IsRigidMask(RecordingGpuCommandList.IndexedDraw draw) =>
        IsRigidFull(draw) || IsRigidVisible(draw);

    static bool IsRigidFull(RecordingGpuCommandList.IndexedDraw draw) => IsPipeline(draw,
        ShaderSources.TargetOutlineMaskVert, ShaderSources.TargetOutlineFullMaskFrag);
    static bool IsSkinnedFull(RecordingGpuCommandList.IndexedDraw draw) => IsPipeline(draw,
        ShaderSources.TargetOutlineSkinnedMaskVert, ShaderSources.TargetOutlineFullMaskFrag);
    static bool IsRigidVisible(RecordingGpuCommandList.IndexedDraw draw) => IsPipeline(draw,
        ShaderSources.TargetOutlineMaskVert, ShaderSources.TargetOutlineVisibleMaskFrag);
    static bool IsSkinnedVisible(RecordingGpuCommandList.IndexedDraw draw) => IsPipeline(draw,
        ShaderSources.TargetOutlineSkinnedMaskVert, ShaderSources.TargetOutlineVisibleMaskFrag);

    static bool IsPipeline(RecordingGpuCommandList.IndexedDraw draw, string vertex, string fragment) =>
        draw.Pipeline is FakePipeline { Request: { } request }
        && request.VertexGlsl == vertex && request.FragmentGlsl == fragment;

    static bool IsOutlineMask(RecordingGpuCommandList.IndexedDraw draw) =>
        draw.Pipeline is FakePipeline { Request: { } request }
        && (request.FragmentGlsl == ShaderSources.TargetOutlineFullMaskFrag
            || request.FragmentGlsl == ShaderSources.TargetOutlineVisibleMaskFrag);

    static SkinnedGltfMesh Tube() =>
        SkinnedMeshBuilder.BuildTube(0.25f, 2f, 4, 6, 3, Axis.Z);

    static Matrix4x4[] BentPose(SkinnedGltfMesh tube, float radians)
    {
        Matrix4x4[] pose = (Matrix4x4[])tube.RestPose.Clone();
        pose[1] = Matrix4x4.CreateRotationX(radians) * pose[1];
        return pose;
    }

    static Matrix4x4[] Compose(SkinnedGltfMesh tube, ReadOnlySpan<Matrix4x4> pose)
    {
        var composed = new Matrix4x4[tube.BoneCount];
        for (int i = 0; i < composed.Length; i++)
            composed[i] = SkinningMath.Compose(pose[i], tube.InverseBind[i]);
        return composed;
    }

    static ModelVertex[] Skin(ReadOnlySpan<SkinnedVertex> source, ReadOnlySpan<Matrix4x4> palette)
    {
        var vertices = new ModelVertex[source.Length];
        for (int i = 0; i < vertices.Length; i++) vertices[i] = SkinningMath.SkinVertex(source[i], palette);
        return vertices;
    }

    static RecordingGpuCommandList.Upload[] CpuVertexUploads(FrameRecord frame, int vertexCount) =>
        frame.Uploads.Where(upload => upload.Bytes == vertexCount * ModelVertex.SizeInBytes).ToArray();

    static void SubmitSkinnedParts(Scene3D scene, SkinnedMeshHandle mesh,
        ReadOnlySpan<Matrix4x4> pose, int count)
    {
        MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1.25f);
        for (int i = 0; i < count; i++)
            scene.DrawSkinnedOutline(group, mesh, pose, Matrix4x4.CreateTranslation(i, 0f, 0f));
    }

    static void WarmRigidCapacity(Harness h, MeshHandle rigid, int count)
    {
        h.Record(scene =>
        {
            MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1.25f);
            for (int i = 0; i < count; i++)
                scene.DrawMeshOutline(group, rigid, Matrix4x4.CreateTranslation(i, 0f, 0f));
        });
    }

    static FakeResourceSet FindSet(FakeGpuResourceFactory factory, IGpuBuffer buffer) =>
        Assert.Single(factory.ResourceSets, set => set.Resources.Any(resource =>
            resource is GpuBufferRange range && ReferenceEquals(range.Buffer, buffer)));

    sealed class Harness : IDisposable
    {
        readonly FakeGpuDevice _device = new();
        readonly UniformBufferTrackingGpuDevice _tracker;
        readonly FakeGpuResourceFactory _factory;
        readonly IGpuTexture _targetTexture;
        readonly IGpuFramebuffer _target;
        bool _disposed;

        public Scene3D Scene { get; }
        public FakeGpuResourceFactory Factory => _factory;

        public Harness()
        {
            _tracker = new UniformBufferTrackingGpuDevice(_device);
            _factory = (FakeGpuResourceFactory)_device.Factory;
            _targetTexture = _tracker.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                32, 24, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _target = _tracker.Factory.CreateFramebuffer(null, _targetTexture);
            Scene = new Scene3D(_tracker, _target.Outputs) { UseGpuSkinning = true };
            Scene.Post.Starfield = false;
            Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
            Scene.Camera.Frame(Vector3.Zero, new Vector3(4f, 3f, 4f));
        }

        public FrameRecord Record(Action<Scene3D> submit)
        {
            FrameRecord baseline = RecordOne(null, null, captureFailure: false);
            return RecordOne(submit, baseline, captureFailure: false);
        }

        public FrameRecord RecordFailure(Action<Scene3D> submit)
        {
            FrameRecord baseline = RecordOne(null, null, captureFailure: false);
            return RecordOne(submit, baseline, captureFailure: true);
        }

        FrameRecord RecordOne(Action<Scene3D>? submit, FrameRecord? baseline, bool captureFailure)
        {
            int bufferStart = _factory.Buffers.Count;
            Scene.Begin();
            submit?.Invoke(Scene);
            using var recording = new RecordingGpuCommandList(new NullGpuCommandList())
            {
                CapturePayloads = true,
                UniformWindowsOfSet = _tracker.WindowsOf,
            };
            using var tally = new CommandTallyGpuCommandList(recording);
            using var binds = new ResourceSetCaptureCommandList(tally);
            Scene.PrepareFrame();
            Exception? failure = null;
            try
            {
                Scene.RenderInternal(binds, 32, 24, _target);
            }
            catch (Exception ex)
            {
                if (!captureFailure) throw;
                failure = ex;
            }
            var maskDraws = recording.IndexedDraws.Where(IsOutlineMask).ToArray();
            uint[] paletteOffsets = binds.Binds.Where(bind => bind.Slot == 2 && bind.Pipeline is FakePipeline
            {
                Request: { } request
            } && request.VertexGlsl == ShaderSources.TargetOutlineSkinnedMaskVert)
                .Select(bind => bind.DynamicOffset).ToArray();
            return new FrameRecord(tally.Tally, baseline?.Tally ?? new GpuCommandTally(),
                recording.Uploads.ToArray(), recording.Reads.ToArray(), maskDraws, paletteOffsets,
                _factory.Buffers.Skip(bufferStart).ToArray(), Scene.LastFrameStats.DrawCalls,
                baseline?.DrawCalls ?? 0, failure);
        }

        public bool IsUniform(IGpuBuffer buffer) => _tracker.IsUniform(buffer);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Scene.Dispose();
            _target.Dispose();
            _targetTexture.Dispose();
            _tracker.Dispose();
            _device.Dispose();
        }
    }

    sealed record FrameRecord(
        GpuCommandTally Tally,
        GpuCommandTally BaselineTally,
        RecordingGpuCommandList.Upload[] Uploads,
        RecordingGpuCommandList.BoundRead[] Reads,
        IReadOnlyList<RecordingGpuCommandList.IndexedDraw> OutlineMaskDraws,
        uint[] SkinnedPaletteOffsets,
        FakeBuffer[] NewBuffers,
        int DrawCalls,
        int BaselineDrawCalls,
        Exception? Failure)
    {
        public int Delta(GpuCommandKind kind) => Tally[kind] - BaselineTally[kind];
    }

    sealed class ResourceSetCaptureCommandList : IGpuCommandList
    {
        readonly IGpuCommandList _inner;
        IGpuPipeline? _pipeline;

        public ResourceSetCaptureCommandList(IGpuCommandList inner) => _inner = inner;
        public List<ResourceSetBind> Binds { get; } = new();
        public void SetPipeline(IGpuPipeline p) { _pipeline = p; _inner.SetPipeline(p); }
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
        {
            Binds.Add(new ResourceSetBind(_pipeline, slot, 0));
            _inner.SetGraphicsResourceSet(slot, set);
        }
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
        {
            Binds.Add(new ResourceSetBind(_pipeline, slot, dynamicOffset));
            _inner.SetGraphicsResourceSet(slot, set, dynamicOffset);
        }
        public void Begin() => _inner.Begin();
        public void End() => _inner.End();
        public void SetFramebuffer(IGpuFramebuffer fb) => _inner.SetFramebuffer(fb);
        public void ClearColorTarget(uint index, Color rgba) => _inner.ClearColorTarget(index, rgba);
        public void ClearDepthStencil(float depth) => _inner.ClearDepthStencil(depth);
        public void SetVertexBuffer(uint slot, IGpuBuffer b) => _inner.SetVertexBuffer(slot, b);
        public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes) =>
            _inner.SetVertexBuffer(slot, b, offsetBytes);
        public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt) => _inner.SetIndexBuffer(b, fmt);
        public void SetScissorRect(uint index, uint x, uint y, uint w, uint h) =>
            _inner.SetScissorRect(index, x, y, w, h);
        public void SetFullScissorRects() => _inner.SetFullScissorRects();
        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart) =>
            _inner.Draw(vertexCount, instanceCount, vertexStart, instanceStart);
        public void Draw(uint vertexCount) => _inner.Draw(vertexCount);
        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset,
            uint instanceStart) => _inner.DrawIndexed(indexCount, instanceCount, indexStart, vertexOffset, instanceStart);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged =>
            _inner.UpdateBuffer(b, offsetBytes, in data);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged =>
            _inner.UpdateBuffer(b, offsetBytes, data);
        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes,
            uint sizeInBytes) => _inner.CopyBuffer(src, srcOffsetBytes, dst, dstOffsetBytes, sizeInBytes);
        public void CopyTexture(IGpuTexture src, IGpuTexture dst) => _inner.CopyTexture(src, dst);
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
            IGpuTexture dst, uint width, uint height) =>
            _inner.CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, width, height);
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst,
            uint dstMipLevel, uint dstArrayLayer, uint width, uint height) =>
            _inner.CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, dstMipLevel, dstArrayLayer,
                width, height);
        public void GenerateMipmaps(IGpuTexture texture) => _inner.GenerateMipmaps(texture);
        public void ResolveTexture(IGpuTexture src, IGpuTexture dst) => _inner.ResolveTexture(src, dst);
        public void SetComputePipeline(IGpuComputePipeline p) => _inner.SetComputePipeline(p);
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set) =>
            _inner.SetComputeResourceSet(slot, set);
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) =>
            _inner.SetComputeResourceSet(slot, set, dynamicOffset);
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) =>
            _inner.Dispatch(groupCountX, groupCountY, groupCountZ);
        public void Dispose() { }
    }

    readonly record struct ResourceSetBind(IGpuPipeline? Pipeline, uint Slot, uint DynamicOffset);
}
