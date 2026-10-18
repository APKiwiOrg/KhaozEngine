using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class ComputePreciseMathGpuTests
    {
        [GpuFact]
        public void DynamicArithmeticPreservesIeeeZeroNaNAndInfinity()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice device = gpu.GpuDevice;
            IGpuResourceFactory factory = device.Factory;
            var words = new uint[PreciseMathShaders.IeeeRows.Length * 3];
            var poison = new uint[PreciseMathShaders.IeeeRows.Length];
            Array.Fill(poison, 0xdeadbeefu);
            for (int i = 0; i < poison.Length; i++)
            {
                var row = PreciseMathShaders.IeeeRows[i];
                words[i * 3] = row.Op;
                words[i * 3 + 1] = row.Left;
                words[i * 3 + 2] = row.Right;
            }
            using IGpuBuffer inputs = factory.CreateBuffer(new GpuBufferDescription(
                (uint)words.Length * sizeof(uint), GpuBufferUsage.StructuredBufferReadOnly, sizeof(uint)));
            using IGpuBuffer results = factory.CreateBuffer(new GpuBufferDescription(
                (uint)poison.Length * sizeof(uint), GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));
            device.UpdateBuffer(inputs, 0, words);
            device.UpdateBuffer(results, 0, poison);
            using IGpuResourceLayout layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("InputBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute),
                new GpuResourceLayoutElement("ResultBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
            using IGpuResourceSet set = factory.CreateResourceSet(new GpuResourceSetDescription(layout, inputs, results));
            using IGpuComputeShader shader = factory.CreateComputeShaderFromSpirv(
                PreciseMathShaders.IeeeCompute, new GpuShaderCompileOptions(PreciseMath: true));
            using IGpuComputePipeline pipeline = factory.CreateComputePipeline(new GpuComputePipelineDescription(shader, layout));
            using IGpuCommandList commands = factory.CreateCommandList();
            commands.Begin();
            commands.SetComputePipeline(pipeline);
            commands.SetComputeResourceSet(0, set);
            commands.Dispatch((uint)poison.Length, 1, 1);
            commands.End();
            device.Submit(commands);
            device.WaitForIdle();
            uint[] actual = GpuReadback.ReadBuffer<uint>(device, results, poison.Length);
            for (int i = 0; i < actual.Length; i++)
            {
                uint? expected = PreciseMathShaders.IeeeRows[i].Result;
                Assert.True(expected.HasValue ? actual[i] == expected.Value
                    : (actual[i] & 0x7f800000u) == 0x7f800000u && (actual[i] & 0x007fffffu) != 0,
                    $"IEEE row {i}: expected {expected?.ToString("x8") ?? "NaN"}, actual {actual[i]:x8}");
            }
        }

        [GpuFact]
        public void ComputeAndVertexKeepSeparateMultiplyAdd()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice device = gpu.GpuDevice;
            Assert.True(device.Capabilities.SupportsCompute);
            IGpuResourceFactory factory = device.Factory;
            using IGpuBuffer inputs = factory.CreateBuffer(new GpuBufferDescription(
                3 * sizeof(uint), GpuBufferUsage.StructuredBufferReadOnly, sizeof(uint)));
            using IGpuBuffer results = factory.CreateBuffer(new GpuBufferDescription(
                sizeof(uint), GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));
            device.UpdateBuffer(inputs, 0, new uint[] { 0x3f800001u, 0x3f7ffffeu, 0xbf800000u });
            using IGpuResourceLayout computeLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("InputBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute),
                new GpuResourceLayoutElement("ResultBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
            using IGpuResourceSet computeSet = factory.CreateResourceSet(new GpuResourceSetDescription(computeLayout, inputs, results));
            using IGpuResourceLayout vertexLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("InputBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Vertex)));
            using IGpuResourceSet vertexSet = factory.CreateResourceSet(new GpuResourceSetDescription(vertexLayout, inputs));
            using IGpuTexture target = factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer framebuffer = factory.CreateFramebuffer(null, target);

            foreach (GpuShaderCompileOptions options in new[] { default, new GpuShaderCompileOptions(true), default, new GpuShaderCompileOptions(true) })
            {
                using IGpuComputeShader compute = factory.CreateComputeShaderFromSpirv(PreciseMathShaders.Compute, options);
                using IGpuShaderSet pair = factory.CreateShadersFromSpirv(PreciseMathShaders.Vertex, PreciseMathShaders.Fragment, options);
                if (!options.PreciseMath) continue;
                device.UpdateBuffer(results, 0, new uint[] { 0xdeadbeefu });
                using IGpuComputePipeline computePipeline = factory.CreateComputePipeline(new GpuComputePipelineDescription(compute, computeLayout));
                using IGpuPipeline graphicsPipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
                {
                    BlendFactor = Vector4.Zero,
                    BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                    DepthStencil = GpuDepthStencilState.Disabled,
                    Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
                    Topology = GpuPrimitiveTopology.TriangleList,
                    ResourceLayouts = new[] { vertexLayout },
                    ShaderSet = pair,
                    VertexLayouts = new List<GpuVertexLayoutDescription>(),
                    Outputs = framebuffer.Outputs,
                });
                using IGpuCommandList commands = factory.CreateCommandList();
                commands.Begin();
                commands.SetComputePipeline(computePipeline);
                commands.SetComputeResourceSet(0, computeSet);
                commands.Dispatch(1, 1, 1);
                commands.SetFramebuffer(framebuffer);
                commands.ClearColorTarget(0, Color.Black);
                commands.SetPipeline(graphicsPipeline);
                commands.SetGraphicsResourceSet(0, vertexSet);
                commands.Draw(3, 1, 0, 0);
                commands.End();
                device.Submit(commands);
                device.WaitForIdle();

                uint[] result = GpuReadback.ReadBuffer<uint>(device, results, 1);
                Assert.Equal(0u, result[0]);
                byte[] rgba = GpuReadback.ToRgba(device, target, 16, 16);
                int center = (8 * 16 + 8) * 4;
                byte[] centerRgba = { rgba[center], rgba[center + 1], rgba[center + 2], rgba[center + 3] };
                Assert.Equal(new byte[] { 0, 255, 0, 255 }, centerRgba);
            }
        }
    }
}
