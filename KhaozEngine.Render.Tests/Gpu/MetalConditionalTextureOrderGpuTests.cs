using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Proves the native Metal backend binds sampled textures by authored binding index even when shader control
    /// flow encounters the higher binding first. Both sides of the runtime branch are read back so compilation
    /// cannot erase the conditional distinction.
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class MetalConditionalTextureOrderGpuTests
    {
        const uint Size = 4;

        const string ConditionalOutOfOrderFrag = @"#version 450
layout(set = 0, binding = 0) uniform texture2D LowerTexture;
layout(set = 0, binding = 1) uniform sampler LowerSampler;
layout(set = 0, binding = 2) uniform texture2D HigherTexture;
layout(set = 0, binding = 3) uniform sampler HigherSampler;
layout(location = 0) out vec4 oColor;

void main() {
    vec4 higher = vec4(0.0);
    if (gl_FragCoord.x >= 2.0) {
        higher = textureLod(sampler2D(HigherTexture, HigherSampler), vec2(0.5), 0.0);
    }
    vec4 lower = textureLod(sampler2D(LowerTexture, LowerSampler), vec2(0.5), 0.0);
    oColor = vec4(lower.r, higher.g, lower.b + higher.b, 1.0);
}
";

        readonly ITestOutputHelper _out;

        public MetalConditionalTextureOrderGpuTests(ITestOutputHelper output) => _out = output;

        [GpuFact]
        public void ConditionalHigherBindingSampleBeforeLowerBindingReadsTheDeclaredTextures()
        {
            if (!MetalDormancy.NativeDeviceAvailable(_out)) return;

            using GpuDeviceContext native = GpuDeviceContext.CreateHeadless(GpuBackendKind.MetalNative);
            Assert.Equal(GpuBackendKind.MetalNative, native.Backend);

            IGpuDevice device = native.GpuDevice;
            IGpuResourceFactory factory = device.Factory;
            using IGpuTexture lower = factory.CreateTexture(GpuTextureDescription.Texture2D(
                1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            using IGpuTexture higher = factory.CreateTexture(GpuTextureDescription.Texture2D(
                1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            device.UpdateTexture(lower, [255, 0, 0, 255], 0, 0, 1, 1);
            device.UpdateTexture(higher, [0, 255, 0, 255], 0, 0, 1, 1);

            using IGpuTexture target = factory.CreateTexture(GpuTextureDescription.Texture2D(
                Size, Size, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer framebuffer = factory.CreateFramebuffer(null, target);
            using IGpuResourceLayout layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("LowerTexture", GpuResourceKind.TextureReadOnly,
                    GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("LowerSampler", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("HigherTexture", GpuResourceKind.TextureReadOnly,
                    GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("HigherSampler", GpuResourceKind.Sampler,
                    GpuShaderStages.Fragment)));
            using IGpuResourceSet set = factory.CreateResourceSet(new GpuResourceSetDescription(
                layout, lower, device.PointSampler, higher, device.PointSampler));
            using IGpuShaderSet shaders = factory.CreateShadersFromSpirv(
                ShaderSources.FullscreenVert, ConditionalOutOfOrderFrag);
            using IGpuPipeline pipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = [GpuBlendAttachment.OverrideBlend],
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid,
                    GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = [layout],
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = framebuffer.Outputs,
            });

            using (IGpuCommandList commands = factory.CreateCommandList())
            {
                commands.Begin();
                commands.SetFramebuffer(framebuffer);
                commands.ClearColorTarget(0, Color.Black);
                commands.SetPipeline(pipeline);
                commands.SetGraphicsResourceSet(0, set);
                commands.Draw(3);
                commands.End();
                device.Submit(commands);
                device.WaitForIdle();
            }

            Assert.Null(device.Diagnostics.DeviceLossReason);
            byte[] pixels = GpuReadback.ToRgba(device, target, (int)Size, (int)Size);
            AssertPixel(pixels, x: 0, expected: [255, 0, 0, 255]);
            AssertPixel(pixels, x: 3, expected: [255, 255, 0, 255]);
        }

        static void AssertPixel(byte[] pixels, int x, byte[] expected)
        {
            int offset = ((int)(Size / 2) * (int)Size + x) * 4;
            Assert.Equal(expected, pixels[offset..(offset + 4)]);
        }
    }
}
