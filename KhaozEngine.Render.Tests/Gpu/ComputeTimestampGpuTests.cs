using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class ComputeTimestampGpuTests(ITestOutputHelper output)
    {
        const uint Size = 16;
        const string Writer = @"#version 450
layout(local_size_x = 1) in;
layout(std430, set = 0, binding = 0) buffer WordBuf { uint Words[]; };
void main() { Words[0] = 17u; }";

        const string Vertex = @"#version 450
layout(std430, set = 0, binding = 0) readonly buffer WordBuf { uint Words[]; };
layout(location = 0) out vec4 vColour;
void main() {
    vec2 corners[3] = vec2[](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
    gl_Position = vec4(corners[gl_VertexIndex], 0.0, 1.0);
    vColour = Words[0] == 17u ? vec4(0.0, 1.0, 0.0, 1.0) : vec4(1.0, 0.0, 0.0, 1.0);
}";

        const string Fragment = @"#version 450
layout(location = 0) in vec4 vColour;
layout(location = 0) out vec4 oColour;
void main() { oColour = vColour; }";

        // Outside the timestamp interval, collect the actual draw pixel without a synchronous texture map.
        const string ReadPixel = @"#version 450
layout(local_size_x = 1) in;
layout(set = 0, binding = 0) uniform texture2D Source;
layout(set = 0, binding = 1) uniform sampler Samp;
layout(std430, set = 0, binding = 2) buffer PixelBuf { uint Pixel[]; };
void main() {
    uvec4 pixel = uvec4(round(texelFetch(sampler2D(Source, Samp), ivec2(8, 8), 0) * 255.0));
    Pixel[0] = pixel.r; Pixel[1] = pixel.g; Pixel[2] = pixel.b; Pixel[3] = pixel.a;
}";

        [Fact]
        public void TimestampPassShadersCompileWithoutADevice()
        {
            ShaderValidation.ValidateCompute(Writer);
            ShaderValidation.ValidatePair(Vertex, Fragment);
            ShaderValidation.ValidateCompute(ReadPixel);
        }

        [GpuFact]
        public void PassBoundaryQueriesEitherResolveOrReportUnsupported() => VerifyPass(repeatResolve: false);

        [GpuFact]
        public void RepeatedResolveReturnsTheSameResult() => VerifyPass(repeatResolve: true);

        void VerifyPass(bool repeatResolve)
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice device = gpu.GpuDevice;
            IGpuResourceFactory factory = device.Factory;
            output.WriteLine($"backend: {device.Backend} ({device.Capabilities.DeviceName})");
            if (device is MetalGpuDevice metal && metal.TimestampApi is { } timestampApi)
                output.WriteLine($"timestamp probe: counterSet={timestampApi.HasTimestampCounterSet}, stageBoundary={timestampApi.SupportsStageBoundary}, blitBoundary={timestampApi.SupportsBlitBoundary}, sampleBufferCreated={timestampApi.SupportsTimestampCounters}");
            if (device is VulkanGpuDevice vulkan)
                output.WriteLine(FormattableString.Invariant($"timestamp probe: validBits={vulkan.TimestampSupport.ValidBits}, periodNanoseconds={vulkan.TimestampSupport.TickPeriodNanoseconds:R}"));
            if (!device.Capabilities.SupportsTimestamps)
            {
                output.WriteLine("timestamp branch: unsupported (pool creation must throw)");
                Assert.False(device.Capabilities.SupportsTimestamps);
                Assert.Throws<NotSupportedException>(() => factory.CreateTimestampPool(4));
                return;
            }

            output.WriteLine("timestamp branch: supported (compute, draw, pending and ready assertions)");
            Assert.True(device.Capabilities.SupportsCompletionFences);
            Assert.True(device.Capabilities.SupportsCompute);
            using IGpuTimestampPool pool = factory.CreateTimestampPool(4);
            Assert.Equal(4u, pool.Capacity);
            using IGpuFence completion = factory.CreateFence();
            using IGpuBuffer words = factory.CreateBuffer(new GpuBufferDescription(
                sizeof(uint), GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));
            using IGpuBuffer pixelWords = factory.CreateBuffer(new GpuBufferDescription(
                4 * sizeof(uint), GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));
            using IGpuBuffer staging = factory.CreateBuffer(new GpuBufferDescription(5 * sizeof(uint), GpuBufferUsage.Staging));
            using IGpuTexture target = factory.CreateTexture(GpuTextureDescription.Texture2D(
                Size, Size, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer framebuffer = factory.CreateFramebuffer(null, target);
            using IGpuTexture probeOutputTarget = factory.CreateTexture(GpuTextureDescription.Texture2D(
                1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
            using IGpuFramebuffer probeFramebuffer = factory.CreateFramebuffer(null, probeOutputTarget);
            using IGpuComputeShader writer = factory.CreateComputeShaderFromSpirv(Writer);
            using IGpuResourceLayout computeLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("WordBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
            using IGpuResourceSet computeSet = factory.CreateResourceSet(new GpuResourceSetDescription(computeLayout, words));
            using IGpuComputePipeline compute = factory.CreateComputePipeline(new GpuComputePipelineDescription(writer, computeLayout));
            using IGpuComputeShader pixelReader = factory.CreateComputeShaderFromSpirv(ReadPixel);
            using IGpuResourceLayout pixelLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Source", GpuResourceKind.TextureReadOnly, GpuShaderStages.Compute),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Compute),
                new GpuResourceLayoutElement("PixelBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
            using IGpuResourceSet pixelSet = factory.CreateResourceSet(new GpuResourceSetDescription(
                pixelLayout, target, device.PointSampler, pixelWords));
            using IGpuComputePipeline readPixel = factory.CreateComputePipeline(new GpuComputePipelineDescription(pixelReader, pixelLayout));
            using IGpuShaderSet shaders = factory.CreateShadersFromSpirv(Vertex, Fragment);
            using IGpuResourceLayout drawLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("WordBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Vertex)));
            using IGpuResourceSet drawSet = factory.CreateResourceSet(new GpuResourceSetDescription(drawLayout, words));
            using IGpuPipeline draw = factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { drawLayout },
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = framebuffer.Outputs,
            });

            using IGpuCommandList commands = factory.CreateCommandList();
            commands.Begin();
            commands.UpdateBuffer(words, 0, 0u);
            commands.BeginTimestamps(pool, 3);
            commands.WriteTimestamp(pool, 0);
            commands.SetComputePipeline(compute);
            commands.SetComputeResourceSet(0, computeSet);
            commands.Dispatch(1, 1, 1);
            commands.WriteTimestamp(pool, 1);
            commands.SetFramebuffer(framebuffer);
            commands.ClearColorTarget(0, Color.Black);
            commands.SetPipeline(draw);
            commands.SetGraphicsResourceSet(0, drawSet);
            commands.Draw(3, 1, 0, 0);
            commands.WriteTimestamp(pool, 2);
            commands.EndTimestamps(pool);
            // Replace the output binding before sampling target in the compute probe.
            commands.SetFramebuffer(probeFramebuffer);
            commands.SetComputePipeline(readPixel);
            commands.SetComputeResourceSet(0, pixelSet);
            commands.Dispatch(1, 1, 1);
            commands.CopyBuffer(words, 0, staging, 0, sizeof(uint));
            commands.CopyBuffer(pixelWords, 0, staging, sizeof(uint), 4 * sizeof(uint));
            commands.End();

            ulong[] ticks = { 99ul, 98ul, 97ul, 96ul };
            Assert.Equal(GpuTimestampStatus.Pending, ResolveChecked(pool, completion, ticks, out double period));
            if (repeatResolve)
                Assert.Equal(GpuTimestampStatus.Pending, ResolveChecked(pool, completion, ticks, out period));

            device.Submit(commands, completion);
            device.WaitForIdle(); // The test alone drains once. D3D11 Submit does not flush.
            Assert.Equal(GpuTimestampStatus.Ready, ResolveChecked(pool, completion, ticks, out period));
            output.WriteLine(FormattableString.Invariant($"timestamp branch: supported, periodNanoseconds={period:R}"));
            if (pool is D3D11TimestampPool d3d11)
                output.WriteLine(FormattableString.Invariant($"timestamp branch: supported, frequency={d3d11.Frequency}, periodNanoseconds={period:R}"));
            Assert.Equal(0ul, ticks[0]);
            Assert.True(ticks[1] >= ticks[0]);
            Assert.True(ticks[2] >= ticks[1]);
            Assert.Equal(96ul, ticks[3]);
            Assert.True(double.IsFinite(period) && period > 0d);
            if (repeatResolve)
            {
                ulong[] repeated = { 11ul, 12ul, 13ul, 96ul };
                Assert.Equal(GpuTimestampStatus.Ready, ResolveChecked(pool, completion, repeated, out double repeatedPeriod));
                Assert.Equal(ticks, repeated);
                Assert.Equal(period, repeatedPeriod);
            }

            byte[] bytes = new byte[5 * sizeof(uint)];
            Assert.True(device.TryReadBuffer(staging, completion, 0, bytes));
            ReadOnlySpan<uint> result = MemoryMarshal.Cast<byte, uint>(bytes);
            Assert.Equal(17u, result[0]);
            Assert.Equal(new uint[] { 0u, 255u, 0u, 255u }, result.Slice(1).ToArray());
        }

        static GpuTimestampStatus ResolveChecked(IGpuTimestampPool pool, IGpuFence completion,
            ulong[] ticks, out double period)
        {
            ulong[] before = (ulong[])ticks.Clone();
            GpuTimestampStatus status = pool.TryResolve(completion, ticks, out period);
            if (status is GpuTimestampStatus.Pending or GpuTimestampStatus.Invalid)
            {
                Assert.Equal(before, ticks);
                Assert.Equal(0d, period);
            }
            return status;
        }
    }
}
