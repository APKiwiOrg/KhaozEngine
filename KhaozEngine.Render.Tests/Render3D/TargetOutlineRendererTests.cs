using System;
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

public sealed class TargetOutlineRendererTests
{
    [Fact]
    public void Partial_mask_allocation_is_disposed_and_the_same_generation_can_retry()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var resources = new RenderResources(device, 64, 64, hdrColor: false);
        using IGpuTexture targetTexture = factory.CreateTexture(GpuTextureDescription.Texture2D(
            64, 64, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
        using IGpuFramebuffer target = factory.CreateFramebuffer(null, targetTexture);
        using var renderer = new TargetOutlineRenderer(device, target.Outputs);
        using IGpuBuffer vertices = factory.CreateBuffer(new GpuBufferDescription(
            3 * ModelVertex.SizeInBytes, GpuBufferUsage.VertexBuffer));
        using IGpuBuffer indices = factory.CreateBuffer(new GpuBufferDescription(
            3 * sizeof(ushort), GpuBufferUsage.IndexBuffer));
        using IGpuCommandList commands = factory.CreateCommandList();
        renderer.EnsureCapacity(1);
        renderer.BeginGroup(Matrix4x4.Identity);
        renderer.EnqueueRigid(vertices, indices, 3, GpuIndexFormat.UInt16, null, 0, Matrix4x4.Identity,
            alphaCutoff: 0f, dissolve: 0f, dissolveComplement: false, renderOrigin: Vector3.Zero);
        int firstMaskTexture = factory.Textures.Count;
        factory.ThrowOnTextureCreate = firstMaskTexture + 2;

        Assert.Throws<InvalidOperationException>(() =>
            renderer.Render(commands, resources, target, Color.White, 1.25f, 0.025f, pixelated: false,
                styleIndex: 0, occluded: true));
        Assert.True(factory.Textures[firstMaskTexture].Disposed);

        factory.ThrowOnTextureCreate = 0;
        renderer.Render(commands, resources, target, Color.White, 1.25f, 0.025f, pixelated: false,
            styleIndex: 0, occluded: true);
    }

    [Fact]
    public void Draw_capacity_growth_can_retry_after_resource_set_creation_fails()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var renderer = new TargetOutlineRenderer(device,
            new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        renderer.EnsureCapacity(1);
        int buffersBefore = factory.Buffers.Count;
        factory.ThrowOnResourceSetCreate = factory.ResourceSets.Count + 1;

        Assert.Throws<InvalidOperationException>(() => renderer.EnsureCapacity(5));
        Assert.True(factory.Buffers[^1].Disposed);

        factory.ThrowOnResourceSetCreate = 0;
        renderer.EnsureCapacity(5);
        Assert.Equal(buffersBefore + 2, factory.Buffers.Count);
    }

    [Fact]
    public void Palette_growth_can_retry_after_resource_set_creation_fails()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var store = new TargetOutlineSkinningStore(device);
        store.EnsurePaletteCapacity(1);
        IGpuResourceSet firstSet = store.PaletteSet;
        int buffersBefore = factory.Buffers.Count;
        factory.ThrowOnResourceSetCreate = factory.ResourceSets.Count + 1;

        Assert.Throws<InvalidOperationException>(() => store.EnsurePaletteCapacity(5));

        Assert.Same(firstSet, store.PaletteSet);
        Assert.True(factory.Buffers[^1].Disposed);
        factory.ThrowOnResourceSetCreate = 0;
        store.EnsurePaletteCapacity(5);
        Assert.Equal(buffersBefore + 2, factory.Buffers.Count);
        Assert.NotSame(firstSet, store.PaletteSet);
    }

    [Fact]
    public void Palette_pack_identity_pads_the_complete_slot()
    {
        using var device = new FakeGpuDevice();
        using var store = new TargetOutlineSkinningStore(device);
        store.EnsurePaletteCapacity(1);
        Matrix4x4 bone = Matrix4x4.CreateTranslation(2f, 3f, 4f);
        store.PackPalette(0, new[] { bone });
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };

        store.UploadPalette(commands);

        RecordingGpuCommandList.Upload upload = Assert.Single(commands.Uploads);
        ReadOnlySpan<Matrix4x4> matrices = MemoryMarshal.Cast<byte, Matrix4x4>(upload.Data!);
        Assert.Equal(bone, matrices[0]);
        Assert.All(matrices.Slice(1, SkinningMath.MaxBonesPerDraw - 1).ToArray(),
            matrix => Assert.Equal(Matrix4x4.Identity, matrix));
    }

    [Fact]
    public void Rigid_and_skinned_mask_pipelines_disable_face_culling_and_reuse_fragments()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var resources = new RenderResources(device, 64, 64, hdrColor: false);
        using IGpuTexture targetTexture = factory.CreateTexture(GpuTextureDescription.Texture2D(
            64, 64, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
        using IGpuFramebuffer target = factory.CreateFramebuffer(null, targetTexture);
        using var renderer = new TargetOutlineRenderer(device, target.Outputs);
        using IGpuBuffer vertices = factory.CreateBuffer(new GpuBufferDescription(
            3 * ModelVertex.SizeInBytes, GpuBufferUsage.VertexBuffer));
        using IGpuBuffer indices = factory.CreateBuffer(new GpuBufferDescription(
            3 * sizeof(ushort), GpuBufferUsage.IndexBuffer));
        using IGpuCommandList commands = factory.CreateCommandList();
        renderer.EnsureCapacity(1);
        renderer.BeginGroup(Matrix4x4.Identity);
        renderer.EnqueueRigid(vertices, indices, 3, GpuIndexFormat.UInt16, null, 0, Matrix4x4.Identity,
            alphaCutoff: 0f, dissolve: 0f, dissolveComplement: false, renderOrigin: Vector3.Zero);
        renderer.Render(commands, resources, target, Color.White, 1.25f, 0.025f, pixelated: false,
            styleIndex: 0, occluded: true);

        FakeGraphicsPipelineRequest[] masks = factory.GraphicsPipelines.Where(request =>
            request.FragmentGlsl == ShaderSources.TargetOutlineFullMaskFrag
            || request.FragmentGlsl == ShaderSources.TargetOutlineVisibleMaskFrag).ToArray();
        Assert.Equal(4, masks.Length);
        Assert.All(masks, request => Assert.Equal(GpuFaceCull.None, request.Description.Rasterizer.CullMode));
        Assert.Equal(2, masks.Count(request => request.VertexGlsl == ShaderSources.TargetOutlineMaskVert));
        Assert.Equal(2, masks.Count(request => request.VertexGlsl == ShaderSources.TargetOutlineSkinnedMaskVert));
    }
}
