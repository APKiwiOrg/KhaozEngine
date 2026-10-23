using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class ComputeUniformAliasGpuTests(ITestOutputHelper output)
{
    const int Size = 16;
    const string Vert = """
        #version 450
        layout(set = 0, binding = 0) uniform Params { vec4 Tint; };
        layout(location = 0) out vec4 colour;
        void main() {
            vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
            colour = Tint;
        }
        """;
    const string Frag = """
        #version 450
        layout(location = 0) in vec4 colour;
        layout(location = 0) out vec4 result;
        void main() { result = colour; }
        """;

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Vertex_uniform_after_retired_storage_uses_new_host_words(bool recorded)
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        var device = gpu.GpuDevice;
        var factory = device.Factory;
        using var prior = factory.CreateBuffer(new(256 * 1024, GpuBufferUsage.StructuredBufferReadWrite, 4));
        using var target = factory.CreateTexture(GpuTextureDescription.Texture2D(Size, Size,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using var framebuffer = factory.CreateFramebuffer(null, target);
        using var layout = factory.CreateResourceLayout(new(new GpuResourceLayoutElement(
            "Params", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));
        using var shaders = factory.CreateShadersFromSpirv(Vert, Frag);
        using var pipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendFactor = Vector4.Zero,
            BlendAttachments = [GpuBlendAttachment.OverrideBlend],
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = [layout],
            ShaderSet = shaders,
            VertexLayouts = [],
            Outputs = framebuffer.Outputs,
        });
        var before = new uint[256 * 1024 / 4];
        Array.Fill(before, 0xfee00000u);
        using (var write = factory.CreateCommandList())
        {
            write.Begin();
            if (recorded) write.UpdateBuffer(prior, 0, before);
            else device.UpdateBuffer(prior, 0, before);
            write.End();
            device.Submit(write);
        }
        device.WaitForIdle();
        prior.Dispose();
        device.Present();
        using var parameters = factory.CreateBuffer(new(16, GpuBufferUsage.UniformBuffer));
        using var set = factory.CreateResourceSet(new(layout, parameters));
        using (var draw = factory.CreateCommandList())
        {
            draw.Begin();
            draw.UpdateBuffer(parameters, 0, new Vector4(0, 1, 0, 1));
            draw.SetFramebuffer(framebuffer);
            draw.ClearColorTarget(0, Color.Black);
            draw.SetPipeline(pipeline);
            draw.SetGraphicsResourceSet(0, set);
            draw.Draw(3);
            draw.End();
            device.Submit(draw);
        }
        byte[] pixels = GpuReadback.ToRgba(device, target, Size, Size);
        string? configured = Environment.GetEnvironmentVariable("KE_COVERAGE_EVIDENCE_DIR");
        string root = string.IsNullOrWhiteSpace(configured)
            ? "/tmp/grimhollow-equipment-coverage-task11/fix3" : configured;
        string evidence = Path.Combine(root, "uniform-alias");
        Directory.CreateDirectory(evidence);
        string path = Path.Combine(evidence, $"{device.Backend}-{recorded}.rgba");
        File.WriteAllBytes(path, pixels);
        for (int i = 0; i < pixels.Length; i += 4)
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, pixels.AsSpan(i, 4).ToArray());
        output.WriteLine($"{device.Backend} {device.Capabilities.DeviceName}: vertex uniform, recorded={recorded}, exact pixels={Size * Size}, capture={path}");
        output.WriteLine("Native allocation overlap is platform dependent. Direct allocator and dependency capture tests prove the reuse contract.");
    }
}
