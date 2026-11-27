using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Core.Internal;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.GpuCore;

[Collection("CoreBackend")]
public sealed class CoreDrawTests
{
    private const int Size = 64;

    [NativeGpuFact]
    public void Clear_and_triangle_render_expected_pixels()
        => WithCoreDevice(device =>
        {
            var factory = device.Factory;
            using var target = CreateTarget(factory);
            using var framebuffer = factory.CreateFramebuffer(null, target);
            using var shaders = CreateTriangleShaders(factory);
            using var pipeline = factory.CreateGraphicsPipeline(Describe(framebuffer.Outputs, shaders));
            using var commands = factory.CreateCommandList();
            commands.Begin();
            commands.SetFramebuffer(framebuffer);
            commands.ClearColorTarget(0, new Color(0f, 0f, 1f, 1f));
            commands.SetPipeline(pipeline);
            commands.Draw(3);
            commands.End();
            device.Submit(commands);

            byte[] rgba = GpuReadback.ToRgba(device, target, Size, Size);
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(rgba, 32, 32));
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(rgba, 0, 0));
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(rgba, 8, 56));
        });

    [NativeGpuFact]
    public void Indexed_draws_use_separate_sampled_bindings_and_the_ordered_dynamic_uniform_window()
        => WithCoreDevice(device =>
        {
            var factory = device.Factory;
            using var target = CreateTarget(factory);
            using var depth = factory.CreateTexture(GpuTextureDescription.Texture2D(Size, Size,
                GpuPixelFormat.R32Float, GpuTextureUsage.DepthStencil));
            using var framebuffer = factory.CreateFramebuffer(depth, target);
            using var texture = factory.CreateTexture(GpuTextureDescription.Texture2D(1, 1,
                GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            device.UpdateTexture(texture, new byte[] { 127, 255, 191, 255 }, 0, 0, 1, 1);

            var imageElements = new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("tex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("sam", GpuResourceKind.Sampler, GpuShaderStages.Fragment));
            using var imageLayout = factory.CreateResourceLayout(imageElements);
            using var compatibleImageLayout = factory.CreateResourceLayout(imageElements);
            using var uniformLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("tint", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment, dynamic: true)));
            using var imageSet = factory.CreateResourceSet(new GpuResourceSetDescription(compatibleImageLayout,
                texture, device.PointSampler));
            uint dynamicOffset = checked((uint)device.UniformOffsetAlignment);
            using var uniform = factory.CreateBuffer(new GpuBufferDescription(dynamicOffset + 16, GpuBufferUsage.UniformBuffer));
            using var uniformSet = factory.CreateResourceSet(new GpuResourceSetDescription(uniformLayout,
                new GpuBufferRange(uniform, 0, 16)));
            using var vertices = factory.CreateBuffer(new GpuBufferDescription(32, GpuBufferUsage.VertexBuffer));
            using var indices = factory.CreateBuffer(new GpuBufferDescription(6, GpuBufferUsage.IndexBuffer));
            using var shaders = factory.CreateShadersFromSpirv(
                """
                #version 450
                layout(location = 0) in vec2 position;
                void main() { gl_Position = vec4(position, 0, 1); }
                """,
                """
                #version 450
                layout(set = 0, binding = 0) uniform texture2D tex;
                layout(set = 0, binding = 1) uniform sampler sam;
                layout(set = 1, binding = 0) uniform Tint { vec4 tint; };
                layout(location = 0) out vec4 colour;
                void main() { colour = texture(sampler2D(tex, sam), vec2(0.5)) * tint; }
                """);
            var description = Describe(framebuffer.Outputs, shaders);
            description.VertexLayouts = [new GpuVertexLayoutDescription(new GpuVertexElement("position", GpuVertexElementFormat.Float2))];
            description.ResourceLayouts = [imageLayout, uniformLayout];
            description.Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, true);
            description.DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual;
            using var pipeline = factory.CreateGraphicsPipeline(description);

            using var commands = factory.CreateCommandList();
            commands.Begin();
            commands.UpdateBuffer<Vector2>(vertices, 8, [new(-1, -1), new(3, -1), new(-1, 3)]);
            commands.UpdateBuffer<ushort>(indices, 0, [0, 1, 2]);
            commands.UpdateBuffer(uniform, 0, new Vector4(0, 1, 0, 1));
            commands.UpdateBuffer(uniform, dynamicOffset, new Vector4(1, 0, 0, 1));
            commands.SetFramebuffer(framebuffer);
            commands.ClearColorTarget(0, new Color(0f, 0f, 0f, 1f));
            commands.ClearDepthStencil(1f);
            commands.SetPipeline(pipeline);
            commands.SetGraphicsResourceSet(0, imageSet);
            commands.SetGraphicsResourceSet(1, uniformSet, dynamicOffset);
            commands.SetVertexBuffer(0, vertices, 8);
            commands.SetIndexBuffer(indices, GpuIndexFormat.UInt16);
            commands.SetScissorRect(0, 0, 0, Size, Size / 2);
            commands.DrawIndexed(3, 1, 0, 0, 0);
            commands.UpdateBuffer(uniform, dynamicOffset, new Vector4(0, 0, 1, 1));
            commands.SetScissorRect(0, 0, Size / 2, Size, Size / 2);
            commands.DrawIndexed(3, 1, 0, 0, 0);
            commands.End();
            device.Submit(commands);

            byte[] rgba = GpuReadback.ToRgba(device, target, Size, Size);
            Assert.Equal(new byte[] { 127, 0, 0, 255 }, Pixel(rgba, 32, 16));
            Assert.Equal(new byte[] { 0, 0, 191, 255 }, Pixel(rgba, 32, 48));
        });

    [NativeGpuFact]
    public void Queued_clear_survives_framebuffer_disposal_before_end()
        => WithCoreDevice(device =>
        {
            var factory = device.Factory;
            using var target = CreateTarget(factory);
            using var framebuffer = factory.CreateFramebuffer(null, target);
            using var commands = factory.CreateCommandList();

            commands.Begin();
            commands.SetFramebuffer(framebuffer);
            commands.ClearColorTarget(0, new Color(0f, 1f, 0f, 1f));
            framebuffer.Dispose();
            commands.End();
            device.Submit(commands);

            byte[] rgba = GpuReadback.ToRgba(device, target, Size, Size);
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(rgba, 32, 32));
        });

    [NativeGpuFact]
    public void Disposed_set_bound_to_a_slot_the_pipeline_declines_is_skipped_at_draw()
        => WithCoreDevice(device =>
        {
            var factory = device.Factory;
            using var target = CreateTarget(factory);
            using var framebuffer = factory.CreateFramebuffer(null, target);
            using var declared = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("tint", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)));
            using var other = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("sam", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
            var set = factory.CreateResourceSet(new GpuResourceSetDescription(other, device.PointSampler));
            using var shaders = CreateTriangleShaders(factory);
            var description = Describe(framebuffer.Outputs, shaders);
            description.ResourceLayouts = [declared];
            using var pipeline = factory.CreateGraphicsPipeline(description);
            using var commands = factory.CreateCommandList();

            commands.Begin();
            commands.SetFramebuffer(framebuffer);
            commands.ClearColorTarget(0, new Color(0f, 0f, 1f, 1f));
            commands.SetPipeline(pipeline);
            commands.SetGraphicsResourceSet(0, set);
            set.Dispose();
            commands.Draw(3);
            commands.End();
            device.Submit(commands);

            byte[] rgba = GpuReadback.ToRgba(device, target, Size, Size);
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(rgba, 32, 32));
        });

    [NativeGpuFact]
    public void Attachment_disposed_during_recording_stays_valid_until_the_work_completes()
        => WithCoreDevice(device =>
        {
            var factory = device.Factory;
            var disposed = CreateTarget(factory);
            using var kept = CreateTarget(factory);
            using var framebuffer = factory.CreateFramebuffer(null, disposed, kept);
            using var commands = factory.CreateCommandList();

            commands.Begin();
            commands.SetFramebuffer(framebuffer);
            commands.ClearColorTarget(0, new Color(1f, 0f, 0f, 1f));
            commands.ClearColorTarget(1, new Color(0f, 1f, 0f, 1f));
            disposed.Dispose();
            commands.End();
            device.Submit(commands);

            byte[] rgba = GpuReadback.ToRgba(device, kept, Size, Size);
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(rgba, 32, 32));
        });

    [NativeGpuFact]
    public void Vertex_buffer_disposed_after_bind_still_feeds_the_draw()
        => WithCoreDevice(device =>
        {
            var factory = device.Factory;
            using var target = CreateTarget(factory);
            using var framebuffer = factory.CreateFramebuffer(null, target);
            var vertices = factory.CreateBuffer(new GpuBufferDescription(24, GpuBufferUsage.VertexBuffer));
            device.UpdateBuffer<Vector2>(vertices, 0, [new(-1, -1), new(3, -1), new(-1, 3)]);
            using var shaders = factory.CreateShadersFromSpirv(
                """
                #version 450
                layout(location = 0) in vec2 position;
                void main() { gl_Position = vec4(position, 0, 1); }
                """,
                """
                #version 450
                layout(location = 0) out vec4 colour;
                void main() { colour = vec4(1, 0, 0, 1); }
                """);
            var description = Describe(framebuffer.Outputs, shaders);
            description.VertexLayouts = [new GpuVertexLayoutDescription(new GpuVertexElement("position", GpuVertexElementFormat.Float2))];
            using var pipeline = factory.CreateGraphicsPipeline(description);
            using var commands = factory.CreateCommandList();

            commands.Begin();
            commands.SetFramebuffer(framebuffer);
            commands.ClearColorTarget(0, new Color(0f, 0f, 1f, 1f));
            commands.SetPipeline(pipeline);
            commands.SetVertexBuffer(0, vertices);
            vertices.Dispose();
            commands.Draw(3);
            commands.End();
            device.Submit(commands);

            byte[] rgba = GpuReadback.ToRgba(device, target, Size, Size);
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(rgba, 32, 32));
        });

    [NativeGpuFact]
    public void Graphics_binds_report_released_native_handles()
        => WithCoreDevice(device =>
        {
            var factory = device.Factory;
            using var target = CreateTarget(factory);
            using var framebuffer = factory.CreateFramebuffer(null, target);
            using var shaders = CreateTriangleShaders(factory);
            using var pipeline = factory.CreateGraphicsPipeline(Describe(framebuffer.Outputs, shaders));
            using var layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription());
            using var set = factory.CreateResourceSet(new GpuResourceSetDescription(layout));
            using var buffer = factory.CreateBuffer(new GpuBufferDescription(16,
                GpuBufferUsage.VertexBuffer | GpuBufferUsage.IndexBuffer));
            pipeline.Dispose();
            framebuffer.Dispose();
            set.Dispose();
            buffer.Dispose();

            using var commands = factory.CreateCommandList();
            commands.Begin();
            AssertReleased(() => commands.SetPipeline(pipeline), "VkPipeline", "graphics pipeline", "binding graphics pipeline");
            AssertReleased(() => commands.SetFramebuffer(framebuffer), "VkImageView", "framebuffer", "binding framebuffer");
            AssertReleased(() => commands.SetGraphicsResourceSet(0, set), "VkDescriptorSet", "resource set", "binding graphics resource set");
            AssertReleased(() => commands.SetVertexBuffer(0, buffer), "VkBuffer", "buffer", "binding vertex buffer");
            AssertReleased(() => commands.SetIndexBuffer(buffer, GpuIndexFormat.UInt16),
                "VkBuffer", "buffer", "binding index buffer");
            commands.End();
        });

    private static void AssertReleased(Action bind, string nativeHandleKind, string resourceKind, string context)
    {
        var refusal = Assert.Throws<GpuResourceOwnershipDisposedException>(bind);
        Assert.Equal(GpuResourceOwnershipRefusal.NativeHandleReleased, refusal.Reason);
        Assert.Equal(nativeHandleKind, refusal.ObjectName);
        Assert.Equal(nativeHandleKind, refusal.NativeHandleKind);
        Assert.Equal(resourceKind, refusal.ResourceKind);
        Assert.Equal(context, refusal.Context);
    }

    private static IGpuShaderSet CreateTriangleShaders(IGpuResourceFactory factory)
        => factory.CreateShadersFromSpirv(
            """
            #version 450
            void main() {
                vec2 points[3] = vec2[](vec2(-1, -1), vec2(1, -1), vec2(0, 1));
                gl_Position = vec4(points[gl_VertexIndex], 0, 1);
            }
            """,
            """
            #version 450
            layout(location = 0) out vec4 colour;
            void main() { colour = vec4(1, 0, 0, 1); }
            """);

    private static GpuPipelineDescription Describe(GpuOutputDescription outputs, IGpuShaderSet shaders) => new()
    {
        ShaderSet = shaders,
        BlendAttachments = [GpuBlendAttachment.OverrideBlend],
        DepthStencil = GpuDepthStencilState.Disabled,
        Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, true, false),
        Topology = GpuPrimitiveTopology.TriangleList,
        VertexLayouts = [],
        ResourceLayouts = [],
        Outputs = outputs,
    };

    private static IGpuTexture CreateTarget(IGpuResourceFactory factory)
        => factory.CreateTexture(GpuTextureDescription.Texture2D(Size, Size,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));

    private static byte[] Pixel(byte[] image, int x, int y)
    {
        int offset = (y * Size + x) * 4;
        return image[offset..(offset + 4)];
    }

    private static void WithCoreDevice(Action<CoreGpuDevice> test)
    {
        CoreBackendRegistration.SeatAsVulkanNative();
        var context = GpuDeviceContext.CreateHeadless(GpuBackendKind.VulkanNative);
        CoreGpuDevice device;
        using (context)
        {
            device = Assert.IsType<CoreGpuDevice>(context.GpuDevice);
            test(device);
        }

        Assert.Equal(0u, device.DisposeValidationErrors);
    }
}
