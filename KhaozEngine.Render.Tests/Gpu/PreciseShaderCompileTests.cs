using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class PreciseShaderCompileTests
    {
        static readonly GpuShaderCompileOptions Precise = new(PreciseMath: true);
        readonly ITestOutputHelper _output;

        public PreciseShaderCompileTests(ITestOutputHelper output) => _output = output;

        // Removing any precision route must break a decoration, emission, policy or cache assertion here.
        [Fact]
        public void OptionsSurviveAllStagesAndCaches()
        {
            Assert.Equal(default, new GpuShaderCompileOptions());
            foreach (GpuShaderStages stage in new[] { GpuShaderStages.Compute, GpuShaderStages.Vertex })
            {
                string source = stage == GpuShaderStages.Compute ? PreciseMathShaders.Compute : PreciseMathShaders.Vertex;
                byte[] defaultSpirv = SpirvFrontEnd.ToSpirv(source, stage, "authored precise default observation");
                _output.WriteLine($"{stage} default compile of authored precise: {NoContractionResults(defaultSpirv).Count} NoContraction decorations (observation only)");
                byte[] spirv = SpirvFrontEnd.ToSpirv(source, stage, "precise probe", Precise);
                AssertSeparateOperations(spirv);
                string hlsl = stage == GpuShaderStages.Compute
                    ? SpirvCrossCompile.ComputeToHlsl(spirv, options: Precise).ComputeSource
                    : SpirvCrossCompile.VertexFragmentToHlsl(spirv, FragmentSpirv(), vertexOptions: Precise).VertexSource;
                string msl = stage == GpuShaderStages.Compute
                    ? SpirvCrossCompile.ComputeToMsl(spirv, options: Precise).ComputeSource
                    : SpirvCrossCompile.VertexFragmentToMsl(spirv, FragmentSpirv(), vertexOptions: Precise).VertexSource;
                Assert.Contains("precise", hlsl);
                Assert.Contains("spvFMul", msl);
                Assert.Contains("spvFAdd", msl);
                _output.WriteLine(stage + " HLSL\n" + hlsl + "\nMSL\n" + msl);
                AssertFrontEndCache(stage, source);
            }

            string defaultIdentity = SpirvFrontEnd.OptionsIdentity("probe");
            string preciseIdentity = SpirvFrontEnd.OptionsIdentity("probe", Precise);
            Assert.Equal(SpirvFrontEndPin.Identity + ";label=probe", defaultIdentity);
            Assert.Equal(Silk.NET.Shaderc.OptimizationLevel.Performance, SpirvFrontEndPin.OptimizationFor(default));
            Assert.Contains(";opt=Performance;", defaultIdentity);
            Assert.Contains(";opt=Zero;", preciseIdentity);
            Assert.EndsWith(";precise=1", preciseIdentity);
            Assert.NotEqual(defaultIdentity, preciseIdentity);
            Assert.Equal(preciseIdentity, SpirvFrontEnd.OptionsIdentity("probe", Precise));

            foreach (MetalShaderStage stage in new[] { MetalShaderStage.Vertex, MetalShaderStage.Compute, MetalShaderStage.Fragment })
            {
                Assert.True(MslCompilePin.FastMathEnabledFor(stage, default));
                Assert.Equal(stage == MetalShaderStage.Fragment, MslCompilePin.FastMathEnabledFor(stage, Precise));
            }
            foreach (D3D11ShaderStage stage in new[] { D3D11ShaderStage.Vertex, D3D11ShaderStage.Compute, D3D11ShaderStage.Fragment })
            {
                const uint requested = 5u;
                uint actual = D3D11ShaderCompileFlags.For(stage, requested, Precise);
                Assert.Equal(stage == D3D11ShaderStage.Fragment ? requested : requested | 0x00002000u, actual);
                Assert.Equal(requested, D3D11ShaderCompileFlags.For(stage, requested, default));
                Assert.Equal(0u, D3D11ShaderCompileFlags.For(stage, requested, default) & 0x00002000u);
                Assert.NotEqual(D3D11ShaderKey.For(stage, requested, PreciseMathShaders.Vertex, PreciseMathShaders.Fragment),
                    D3D11ShaderKey.For(stage, requested, Precise, PreciseMathShaders.Vertex, PreciseMathShaders.Fragment));
            }

            ShaderValidation.ValidatePair(PreciseMathShaders.Vertex, PreciseMathShaders.Fragment, Precise, "precise pair");
            ShaderValidation.ValidateCompute(PreciseMathShaders.Compute, Precise, "precise compute");
        }

        // Injecting NoContraction or serving a qualified module for an unqualified key must fail here.
        [Theory]
        [InlineData(GpuShaderStages.Compute)]
        [InlineData(GpuShaderStages.Vertex)]
        public void UnqualifiedArithmeticNeverGainsNoContractionInColdOrWarmCaches(GpuShaderStages stage)
        {
            string source = (stage == GpuShaderStages.Compute ? PreciseMathShaders.Compute : PreciseMathShaders.Vertex)
                .Replace("precise ", string.Empty, StringComparison.Ordinal);
            var frontEnd = new SpirvCompileCache(enabled: true, capacity: 4);
            using var directory = new CacheDirectory();
            var bytes = new SpirvBytesCache(directory.Path);
            foreach (GpuShaderCompileOptions options in new[] { default, Precise, default, Precise })
            {
                byte[] spirv = frontEnd.GetOrCompile(SpirvFrontEnd.OptionsIdentity("unqualified probe", options), stage, source,
                    () => SpirvFrontEnd.ToSpirv(source, stage, "unqualified probe", options));
                Assert.Empty(NoContractionResults(spirv));

                // A fresh module cache makes the warm disk bytes reach the API for inspection too.
                var api = new FakeVulkanShaderApi();
                var modules = new VulkanShaderModuleCache(api);
                try
                {
                    if (stage == GpuShaderStages.Compute)
                    {
                        using var kernel = new VulkanComputeShader(modules, source, bytesCache: bytes, options: options);
                        Assert.Single(api.Created);
                    }
                    else
                    {
                        using var pair = new VulkanShaderSet(modules, source, PreciseMathShaders.Fragment,
                            bytesCache: bytes, options: options);
                        Assert.Equal(2, api.Created.Count);
                    }
                    Assert.Empty(NoContractionResults(api.Created[0]));
                }
                finally { modules.DestroyAll(); }
                _output.WriteLine($"{stage}, PreciseMath={options.PreciseMath}: no NoContraction in front-end and Vulkan modules");
            }
            Assert.Equal(2L, frontEnd.CompileCount);
            Assert.Equal(2L, frontEnd.HitCount);
            Assert.Equal(stage == GpuShaderStages.Compute ? 2 : 3, bytes.Misses);
            Assert.Equal(stage == GpuShaderStages.Compute ? 2 : 5, bytes.Hits);
            Assert.Equal(stage == GpuShaderStages.Compute ? 2 : 3, bytes.Writes);
        }

        [Fact]
        public void MetalDiskCacheKeepsDefaultAndPreciseProgramsSeparate()
        {
            using var directory = new CacheDirectory();
            var cache = new MetalMslCache(directory.Path);
            foreach (bool compute in new[] { true, false })
            {
                var programs = new List<MetalMslProgram>();
                foreach (GpuShaderCompileOptions options in new[] { default, Precise, default, Precise })
                {
                    programs.Add(compute
                        ? MetalShaderBuild.Compute(PreciseMathShaders.Compute, cache, options: options).Program
                        : MetalShaderBuild.Pair(PreciseMathShaders.Vertex, PreciseMathShaders.Fragment, cache, options: options));
                }
                Assert.Equal(programs[0].Stages[0].Msl, programs[2].Stages[0].Msl);
                Assert.Equal(programs[1].Stages[0].Msl, programs[3].Stages[0].Msl);
                Assert.Contains("spvFMul", programs[1].Stages[0].Msl);
                Assert.Contains("spvFAdd", programs[1].Stages[0].Msl);
                Assert.NotEqual(programs[0].Stages[0].Msl, programs[1].Stages[0].Msl);
            }
            Assert.Equal(4, cache.Misses);
            Assert.Equal(4, cache.Hits);
            Assert.Equal(4, cache.Writes);
        }

        [Fact]
        public void VulkanDiskAndModuleCachesKeepTheRequestedPrecision()
        {
            using var directory = new CacheDirectory();
            var bytes = new SpirvBytesCache(directory.Path);
            var api = new FakeVulkanShaderApi();
            var modules = new VulkanShaderModuleCache(api);
            try
            {
                var compute = new List<ulong>();
                var vertex = new List<ulong>();
                var fragment = new List<ulong>();
                foreach (GpuShaderCompileOptions options in new[] { default, Precise, default, Precise })
                {
                    using var kernel = new VulkanComputeShader(modules, PreciseMathShaders.Compute,
                        bytesCache: bytes, options: options);
                    using var pair = new VulkanShaderSet(modules, PreciseMathShaders.Vertex, PreciseMathShaders.Fragment,
                        bytesCache: bytes, options: options);
                    compute.Add(kernel.Module);
                    vertex.Add(pair.VertexModule);
                    fragment.Add(pair.FragmentModule);
                }
                AssertAlternatingModules(compute);
                AssertAlternatingModules(vertex);
                Assert.All(fragment, module => Assert.Equal(fragment[0], module));
                Assert.Equal(5, bytes.Misses);
                Assert.Equal(7, bytes.Hits);
                Assert.Equal(5, bytes.Writes);
                Assert.Equal(5, api.Created.Count);
                AssertSeparateOperations(api.Created[3]);
                AssertSeparateOperations(api.Created[4]);
            }
            finally { modules.DestroyAll(); }
        }

        [Fact]
        public void LegacyFactoriesForwardDefaultOptionsAndRefusePreciseOptions()
        {
            IGpuResourceFactory factory = new FakeGpuResourceFactory();
            using IGpuShaderSet pair = factory.CreateShadersFromSpirv(PreciseMathShaders.Vertex, PreciseMathShaders.Fragment, default);
            NotSupportedException defaultCompute = Assert.Throws<NotSupportedException>(
                () => factory.CreateComputeShaderFromSpirv(PreciseMathShaders.Compute, default));
            Assert.Contains("SupportsCompute = false", defaultCompute.Message);
            Assert.Throws<NotSupportedException>(() => factory.CreateShadersFromSpirv(
                PreciseMathShaders.Vertex, PreciseMathShaders.Fragment, Precise));
            Assert.Throws<NotSupportedException>(() => factory.CreateComputeShaderFromSpirv(PreciseMathShaders.Compute, Precise));
        }

        static HashSet<uint> NoContractionResults(byte[] spirv)
        {
            ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(spirv);
            Assert.True(words.Length >= 5);
            Assert.Equal(0x07230203u, words[0]);
            var decorated = new HashSet<uint>();
            for (int offset = 5; offset < words.Length;)
            {
                int length = (int)(words[offset] >> 16);
                Assert.True(length > 0 && offset + length <= words.Length);
                if ((words[offset] & 0xffffu) == 71u && length >= 3 && words[offset + 2] == 42u)
                    decorated.Add(words[offset + 1]);
                offset += length;
            }
            return decorated;
        }

        void AssertSeparateOperations(byte[] spirv)
        {
            ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(spirv);
            HashSet<uint> decorated = NoContractionResults(spirv);
            var arithmetic = new List<(uint Opcode, uint Result)>();
            for (int offset = 5; offset < words.Length;)
            {
                int length = (int)(words[offset] >> 16);
                Assert.True(length > 0 && offset + length <= words.Length);
                uint opcode = words[offset] & 0xffffu;
                if (opcode is 129u or 133u)
                    arithmetic.Add((opcode, words[offset + 2]));
                offset += length;
            }
            Assert.Single(arithmetic, op => op.Opcode == 129u);
            Assert.Single(arithmetic, op => op.Opcode == 133u);
            foreach ((uint opcode, uint result) in arithmetic)
            {
                Assert.Contains(result, decorated);
                _output.WriteLine($"OpDecorate %{result} NoContraction (42), arithmetic opcode {opcode}");
            }
        }

        static byte[] FragmentSpirv() => SpirvFrontEnd.ToSpirv(PreciseMathShaders.Fragment, GpuShaderStages.Fragment);

        static void AssertAlternatingModules(IReadOnlyList<ulong> modules)
        {
            Assert.NotEqual(modules[0], modules[1]);
            Assert.Equal(modules[0], modules[2]);
            Assert.Equal(modules[1], modules[3]);
        }

        void AssertFrontEndCache(GpuShaderStages stage, string source)
        {
            var cache = new SpirvCompileCache(enabled: true, capacity: 4);
            var compiled = new List<byte[]>();
            foreach (GpuShaderCompileOptions options in new[] { default, Precise, default, Precise })
            {
                compiled.Add(cache.GetOrCompile(SpirvFrontEnd.OptionsIdentity("probe", options), stage, source,
                    () => SpirvFrontEnd.ToSpirv(source, stage, "probe", options)));
            }
            Assert.Equal(2L, cache.CompileCount);
            Assert.Equal(2L, cache.HitCount);
            Assert.Equal(compiled[0], compiled[2]);
            Assert.Equal(compiled[1], compiled[3]);
            Assert.False(compiled[0].AsSpan().SequenceEqual(compiled[1]));
            AssertSeparateOperations(compiled[3]);
        }

        sealed class CacheDirectory : IDisposable
        {
            internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ke-precise-" + Guid.NewGuid().ToString("N"));
            public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        }
    }
}
