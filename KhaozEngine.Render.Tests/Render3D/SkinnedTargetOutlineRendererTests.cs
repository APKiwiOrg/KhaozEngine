using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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

    sealed class Harness : IDisposable
    {
        readonly FakeGpuDevice _device = new();
        readonly FakeGpuResourceFactory _factory;
        readonly IGpuTexture _targetTexture;
        readonly IGpuFramebuffer _target;

        public Scene3D Scene { get; }

        public Harness()
        {
            _factory = (FakeGpuResourceFactory)_device.Factory;
            _targetTexture = _factory.CreateTexture(GpuTextureDescription.Texture2D(
                32, 24, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _target = _factory.CreateFramebuffer(null, _targetTexture);
            Scene = new Scene3D(_device, _target.Outputs) { UseGpuSkinning = true };
            Scene.Post.Starfield = false;
            Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
            Scene.Camera.Frame(Vector3.Zero, new Vector3(4f, 3f, 4f));
        }

        public FrameRecord Record(Action<Scene3D> submit)
        {
            FrameRecord baseline = RecordOne(null, null);
            return RecordOne(submit, baseline);
        }

        FrameRecord RecordOne(Action<Scene3D>? submit, FrameRecord? baseline)
        {
            int bufferStart = _factory.Buffers.Count;
            Scene.Begin();
            submit?.Invoke(Scene);
            using var recording = new RecordingGpuCommandList(new NullGpuCommandList());
            using var tally = new CommandTallyGpuCommandList(recording);
            using var binds = new ResourceSetCaptureCommandList(tally);
            Scene.PrepareFrame();
            Scene.RenderInternal(binds, 32, 24, _target);
            var maskDraws = recording.IndexedDraws.Where(IsOutlineMask).ToArray();
            uint[] paletteOffsets = binds.Binds.Where(bind => bind.Slot == 2 && bind.Pipeline is FakePipeline
            {
                Request: { } request
            } && request.VertexGlsl == ShaderSources.TargetOutlineSkinnedMaskVert)
                .Select(bind => bind.DynamicOffset).ToArray();
            return new FrameRecord(tally.Tally, baseline?.Tally ?? new GpuCommandTally(),
                recording.Uploads.ToArray(), maskDraws, paletteOffsets,
                _factory.Buffers.Skip(bufferStart).ToArray(), Scene.LastFrameStats.DrawCalls,
                baseline?.DrawCalls ?? 0);
        }

        public void Dispose()
        {
            Scene.Dispose();
            _target.Dispose();
            _targetTexture.Dispose();
            _device.Dispose();
        }
    }

    sealed record FrameRecord(
        GpuCommandTally Tally,
        GpuCommandTally BaselineTally,
        RecordingGpuCommandList.Upload[] Uploads,
        IReadOnlyList<RecordingGpuCommandList.IndexedDraw> OutlineMaskDraws,
        uint[] SkinnedPaletteOffsets,
        FakeBuffer[] NewBuffers,
        int DrawCalls,
        int BaselineDrawCalls)
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
