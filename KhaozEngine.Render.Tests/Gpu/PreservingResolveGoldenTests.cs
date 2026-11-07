using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class PreservingResolveGoldenTests(ITestOutputHelper output)
{
    const int Width = 16, Height = 8;

    [GpuFact(RequiresFourSampleMsaa = true)]
    public void GoldenMsaaPreservingResolveSurvivesFurtherDrawingAndTheFinalResolve()
    {
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice device = gpu.GpuDevice;
        output.WriteLine($"Backend {device.Backend}, device {device.Capabilities.DeviceName}");
        IGpuResourceFactory factory = device.Factory;
        using IGpuTexture msaa = factory.CreateTexture(new GpuTextureDescription(
            Width, Height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget, sampleCount: 4));
        using IGpuTexture early = Target(factory);
        using IGpuTexture final = Target(factory);
        using IGpuFramebuffer framebuffer = factory.CreateFramebuffer(null, msaa);
        using IGpuShaderSet red = factory.CreateShadersFromSpirv(Vertex, Red);
        // Bound green by geometry because a scissor edge under MSAA is not pixel exact on lavapipe.
        using IGpuShaderSet green = factory.CreateShadersFromSpirv(RightHalfVertex, Green);
        using IGpuPipeline redPipeline = Pipeline(factory, framebuffer, red);
        using IGpuPipeline greenPipeline = Pipeline(factory, framebuffer, green);
        using IGpuCommandList commands = factory.CreateCommandList();
        using (GpuRecording.Open(device, commands, "preserving resolve regression"))
        {
            commands.SetFramebuffer(framebuffer);
            commands.ClearColorTarget(0, Color.Black);
            commands.SetPipeline(redPipeline);
            commands.SetScissorRect(0, 0, 0, Width / 2, Height);
            commands.Draw(3);
            commands.ResolveTexture(msaa, early, preserveSource: true);
            commands.SetPipeline(greenPipeline);
            commands.SetScissorRect(0, Width / 2, 0, Width / 2, Height);
            commands.Draw(3);
            commands.ResolveTexture(msaa, final);
        }
        device.Submit(commands);
        device.WaitForIdle();

        byte[] before = GpuReadback.ToRgba(device, early, Width, Height);
        byte[] after = GpuReadback.ToRgba(device, final, Width, Height);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int p = (y * Width + x) * 4;
                bool left = x < Width / 2;
                if (before[p] != (left ? 255 : 0) || before[p + 1] != 0 || before[p + 2] != 0 || before[p + 3] != 255
                    || after[p] != (left ? 255 : 0) || after[p + 1] != (left ? 0 : 255) || after[p + 2] != 0 || after[p + 3] != 255)
                    output.WriteLine($"Pixel ({x}, {y}): early RGBA=({before[p]}, {before[p + 1]}, {before[p + 2]}, {before[p + 3]}), final RGBA=({after[p]}, {after[p + 1]}, {after[p + 2]}, {after[p + 3]})");
            }
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int p = (y * Width + x) * 4;
                bool left = x < Width / 2;
                // The early snapshot stays red/black. The final image retains red and adds green.
                Assert.Equal(left ? (byte)255 : (byte)0, before[p]);
                Assert.Equal((byte)0, before[p + 1]);
                Assert.Equal((byte)0, before[p + 2]);
                Assert.Equal((byte)255, before[p + 3]);
                Assert.Equal(left ? (byte)255 : (byte)0, after[p]);
                Assert.Equal(left ? (byte)0 : (byte)255, after[p + 1]);
                Assert.Equal((byte)0, after[p + 2]);
                Assert.Equal((byte)255, after[p + 3]);
            }
        output.WriteLine("Early image retained red/black. Final image retained red and added green at every pixel.");
    }

    static IGpuTexture Target(IGpuResourceFactory factory) => factory.CreateTexture(GpuTextureDescription.Texture2D(
        Width, Height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));

    static IGpuPipeline Pipeline(IGpuResourceFactory factory, IGpuFramebuffer framebuffer, IGpuShaderSet shaders) =>
        factory.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            ShaderSet = shaders,
            ResourceLayouts = [],
            VertexLayouts = [],
            BlendAttachments = [GpuBlendAttachment.OverrideBlend],
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid,
                GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: true),
            Topology = GpuPrimitiveTopology.TriangleList,
            Outputs = framebuffer.Outputs,
        });

    const string Vertex = """
        #version 450
        void main() {
            vec2 p = vec2(float((gl_VertexIndex << 1) & 2), float(gl_VertexIndex & 2));
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;
    const string Red = """
        #version 450
        layout(location=0) out vec4 Color;
        void main() { Color = vec4(1, 0, 0, 1); }
        """;
    const string RightHalfVertex = """
        #version 450
        void main() {
            vec2 p = vec2(float((gl_VertexIndex << 1) & 2), float(gl_VertexIndex & 2));
            gl_Position = vec4(p.x, p.y * 2.0 - 1.0, 0.0, 1.0);
        }
        """;
    const string Green = """
        #version 450
        layout(location=0) out vec4 Color;
        void main() { Color = vec4(0, 1, 0, 1); }
        """;
}
