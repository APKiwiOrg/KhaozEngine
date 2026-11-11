using System;
using System.IO;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class D3D11PreciseShaderWorkerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Workers_partition_precise_programs_and_preserve_options_on_cache_hits(bool compute, bool cached)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ke-precise-workers-" + Guid.NewGuid().ToString("N"));
        var cache = new D3D11DxbcCache(directory);
        using var release = new ManualResetEventSlim();
        var build = new BuildApi(release);
        var compiler = Compiler(build, cache);
        try
        {
            if (cached)
            {
                Assert.True(cache.TryWrite(Key(compute, default), new byte[] { 0 }));
                Assert.True(cache.TryWrite(Key(compute, new(true)), new byte[] { 1 }));
            }
            // Hold the first compile until both requests are queued. A missing policy in the flight key
            // would deterministically serve its bytes to the second request, even with several workers.
            if (compute)
            {
                using var ordinary = (D3D11ComputeShader)compiler.CreateComputeShader("compute");
                using var precise = (D3D11ComputeShader)compiler.CreateComputeShader("compute", new(true));
                release.Set();
                Assert.Equal(new byte[] { 0 }, ordinary.Bytecode.ToArray());
                Assert.Equal(new byte[] { 1 }, precise.Bytecode.ToArray());
                Assert.Contains("Flags1=0x00002000", precise.DiagnosticEvidence!.Flags);
            }
            else
            {
                using var ordinary = compiler.CreateShaderSet("vertex", "pixel");
                using var precise = compiler.CreateShaderSet("vertex", "pixel", new(true));
                release.Set();
                Assert.Equal(new byte[] { 0 }, ordinary.VertexShaderBytecode.ToArray());
                Assert.Equal(new byte[] { 1 }, precise.VertexShaderBytecode.ToArray());
                Assert.Contains("Flags1=0x00002000", ((D3D11ShaderSet)precise).VertexEvidence!.Flags);
                Assert.Contains("Flags1=0x00000000", ((D3D11ShaderSet)precise).PixelEvidence!.Flags);
            }
            Assert.Equal(new byte[] { 0 }, cache.TryRead(Key(compute, default)));
            Assert.Equal(new byte[] { 1 }, cache.TryRead(Key(compute, new(true))));
        }
        finally
        {
            release.Set();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_precise_flights_retry_with_the_same_policy(bool compute)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ke-precise-retry-" + Guid.NewGuid().ToString("N"));
        var cache = new D3D11DxbcCache(directory);
        using var release = new ManualResetEventSlim(true);
        var build = new BuildApi(release) { FailNext = true };
        var compiler = Compiler(build, cache);
        try
        {
            if (compute)
            {
                using var failed = (D3D11ComputeShader)compiler.CreateComputeShader("compute", new(true));
                Assert.Throws<ShaderValidationException>(() => failed.Bytecode);
                using var retry = (D3D11ComputeShader)compiler.CreateComputeShader("compute", new(true));
                Assert.Equal(new byte[] { 1 }, retry.Bytecode.ToArray());
            }
            else
            {
                using var failed = compiler.CreateShaderSet("vertex", "pixel", new(true));
                Assert.Throws<ShaderValidationException>(() => failed.VertexShaderBytecode);
                using var retry = compiler.CreateShaderSet("vertex", "pixel", new(true));
                Assert.Equal(new byte[] { 1 }, retry.VertexShaderBytecode.ToArray());
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    static D3D11ShaderCompiler Compiler(BuildApi build, D3D11DxbcCache cache) =>
        new(new DeviceLiveness(), new DeviceApi(build), cache, 0,
            new(false, D3D11RecordMode.Deferred, false, false), null!);

    static string Key(bool compute, GpuShaderCompileOptions options) => compute
        ? D3D11ShaderKey.For(D3D11ShaderStage.Compute, 0, options, "compute")
        : D3D11ShaderKey.For(D3D11ShaderStage.Vertex, 0, options, "vertex", "pixel");

    sealed class DeviceApi(BuildApi build) : ID3D11ShaderCompilerApi
    {
        public ID3D11ShaderBuildApi Compilation => build;
        public D3D11CompiledPair Pair(string vertex, string pixel, uint flags, D3D11DxbcCache? cache) => throw new InvalidOperationException();
        public D3D11CompiledCompute Compute(string source, uint flags, D3D11DxbcCache? cache) => throw new InvalidOperationException();
        public object CreateShader(byte[] dxbc, D3D11ShaderStage stage) => throw new InvalidOperationException();
    }

    sealed class BuildApi(ManualResetEventSlim release) : ID3D11ShaderBuildApi
    {
        internal bool FailNext;
        public D3D11CompiledPair Pair(string vertex, string pixel, uint flags, D3D11DxbcCache? cache) => Pair(vertex, pixel, flags, cache, default);
        public D3D11CompiledCompute Compute(string source, uint flags, D3D11DxbcCache? cache) => Compute(source, flags, cache, default);
        public D3D11CompiledPair Pair(string vertex, string pixel, uint flags, D3D11DxbcCache? cache, GpuShaderCompileOptions options)
            => new(Compile(false, cache!, options), new byte[] { 2 });
        public D3D11CompiledCompute Compute(string source, uint flags, D3D11DxbcCache? cache, GpuShaderCompileOptions options)
            => new(Compile(true, cache!, options), 1, 1, 1);
        byte[] Compile(bool compute, D3D11DxbcCache cache, GpuShaderCompileOptions options)
        {
            release.Wait();
            if (FailNext) { FailNext = false; throw new ShaderValidationException("synthetic compile failure"); }
            string key = Key(compute, options);
            byte[]? bytes = cache.TryRead(key);
            if (bytes is not null) return bytes;
            bytes = [options.PreciseMath ? (byte)1 : (byte)0];
            Assert.True(cache.TryWrite(key, bytes));
            return bytes;
        }
    }
}
