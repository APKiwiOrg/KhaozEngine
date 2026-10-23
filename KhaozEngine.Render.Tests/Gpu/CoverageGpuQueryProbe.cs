using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageGpuQueryProbe
{
    internal static uint[] RunOperations(ITestOutputHelper output, IEnumerable<ShippedComputeKernel> kernels,
        uint[] inputs, int inputWords, int outputWords, uint[]? staticWords = null)
    {
        var combined = new uint[inputs[0] * outputWords];
        int operation = 0;
        foreach (var kernel in kernels)
        {
            uint[] actual = Run(output, kernel.Name, kernel.ComputeGlsl, inputs, combined.Length, staticWords);
            int rows = 0;
            for (int row = 0; row < inputs[0]; row++)
            {
                if (inputs[1 + row * inputWords] != operation) continue;
                Array.Copy(actual, row * outputWords, combined, row * outputWords, outputWords); rows++;
            }
            Assert.True(rows > 0, $"{kernel.Name}: empty physical probe");
            operation++;
        }
        return combined;
    }

    internal static uint[] Run(ITestOutputHelper output, string name, string text, uint[] inputs, int count, uint[]? staticWords = null, GpuShaderCompileOptions? options = null)
    {
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice device = gpu.GpuDevice;
        Assert.True(device.Capabilities.SupportsCompute);
        output.WriteLine($"{name}: native backend {gpu.Backend}, adapter {device.Capabilities.DeviceName}");
        IGpuResourceFactory factory = device.Factory;
        using IGpuBuffer source = factory.CreateBuffer(new((uint)inputs.Length * 4, GpuBufferUsage.StructuredBufferReadOnly, 4));
        using IGpuBuffer result = factory.CreateBuffer(new((uint)count * 4, GpuBufferUsage.StructuredBufferReadWrite, 4));
        using IGpuBuffer data = factory.CreateBuffer(new((uint)(staticWords?.Length ?? 1) * 4, GpuBufferUsage.StructuredBufferReadOnly, 4));
        device.UpdateBuffer(source, 0, inputs);
        if (staticWords != null) device.UpdateBuffer(data, 0, staticWords);
        var poison = new uint[count];
        Array.Fill(poison, 0xdeadbeefu);
        device.UpdateBuffer(result, 0, poison);
        GpuResourceLayoutElement[] elements = staticWords == null
            ? [new("InputBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute),
                new("ResultBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)]
            : [new("InputBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute),
                new("ResultBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
                new("StaticBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute)];
        using IGpuResourceLayout layout = factory.CreateResourceLayout(new(elements));
        using IGpuResourceSet set = staticWords == null
            ? factory.CreateResourceSet(new(layout, source, result)) : factory.CreateResourceSet(new(layout, source, result, data));
        var precise = options ?? new GpuShaderCompileOptions(PreciseMath: true);
        byte[] spirv = SpirvFrontEnd.ToSpirv(text, GpuShaderStages.Compute, name, precise);
        CoverageSpirvAccessAudit.Validate(spirv, name, constantPrivateArrays: true);
        ShaderValidation.ValidateCompute(text, precise, name);
        string? directory = Environment.GetEnvironmentVariable("KE_SHADER_CORPUS_DUMP");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, name + ".compute.spirv"), spirv);
            var hlsl = SpirvCrossCompile.ComputeToHlsl(spirv, name, precise);
            var msl = SpirvCrossCompile.ComputeToMsl(spirv, name, precise);
            File.WriteAllText(Path.Combine(directory, name + ".compute.hlsl"), hlsl.ComputeSource.ReplaceLineEndings("\n"));
            File.WriteAllText(Path.Combine(directory, name + ".compute.msl"), msl.ComputeSource.ReplaceLineEndings("\n"));
            string shape = ShaderCorpus.Canonical(hlsl.Reflection);
            Assert.Equal(shape, ShaderCorpus.Canonical(msl.Reflection));
            var rows = new List<string>();
            foreach (string target in new[] { "hlsl", "msl", "spirv" })
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(directory, name + ".compute." + target));
                rows.Add($"{name}.compute.{target} {Convert.ToHexStringLower(SHA256.HashData(bytes))} {bytes.Length}");
            }
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shape)));
            rows.Add($"{name}.layout.hlsl {hash} {elements.Length} {shape}");
            rows.Add($"{name}.layout.msl {hash} {elements.Length} {shape}");
            File.WriteAllLines(Path.Combine(directory, name + ".corpus-rows.txt"), rows);
        }
        using IGpuComputeShader shader = factory.CreateComputeShaderFromSpirv(text, precise);
        using IGpuComputePipeline pipeline = factory.CreateComputePipeline(new(shader, layout));
        if (OperatingSystem.IsMacOS() && pipeline is MetalComputePipeline metal)
        {
            output.WriteLine($"{name}: maxTotalThreadsPerThreadgroup={metal.State.MaxTotalThreadsPerThreadgroup}, threadExecutionWidth={metal.State.ThreadExecutionWidth}");
            Assert.True(metal.State.MaxTotalThreadsPerThreadgroup >= 64);
            Assert.True(metal.State.ThreadExecutionWidth > 0);
        }
        using IGpuCommandList commands = factory.CreateCommandList();
        commands.Begin(); commands.SetComputePipeline(pipeline); commands.SetComputeResourceSet(0, set);
        commands.Dispatch((inputs[0] + 63) / 64, 1, 1); commands.End();
        device.Submit(commands); device.WaitForIdle();
        return GpuReadback.ReadBuffer<uint>(device, result, count);
    }
}
