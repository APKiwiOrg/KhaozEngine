using System;
using System.Numerics;
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
        renderer.Enqueue(vertices, indices, 3, GpuIndexFormat.UInt16, null, 0, Matrix4x4.Identity,
            alphaCutoff: 0f, dissolve: 0f, dissolveComplement: false, renderOrigin: Vector3.Zero);
        int firstMaskTexture = factory.Textures.Count;
        factory.ThrowOnTextureCreate = firstMaskTexture + 2;

        Assert.Throws<InvalidOperationException>(() =>
            renderer.Render(commands, resources, target, Color.White, 1.25f, 0.025f, styleIndex: 0));
        Assert.True(factory.Textures[firstMaskTexture].Disposed);

        factory.ThrowOnTextureCreate = 0;
        renderer.Render(commands, resources, target, Color.White, 1.25f, 0.025f, styleIndex: 0);
    }
}
