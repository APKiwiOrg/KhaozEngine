using System;
using KhaozEngine.Gpu;
using KhaozEngine.Tests.Gpu;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Records compute ownership only. These handles never execute or synthesize numerical results.</summary>
internal sealed class CoverageRecordingFactory(FakeGpuResourceFactory inner, bool failCoverageBuild) : IGpuResourceFactory
{
    internal int CoverageBuildAttempts { get; private set; }
    string? firstCoverageSource;
    int coverageShaderAttempts;
    internal readonly System.Collections.Generic.List<Shader> Shaders = new();
    public IGpuBuffer CreateBuffer(in GpuBufferDescription d) => inner.CreateBuffer(d);
    public IGpuTexture CreateTexture(in GpuTextureDescription d) => inner.CreateTexture(d);
    public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour) => inner.CreateFramebuffer(depth, colour);
    public IGpuSampler CreateSampler(in GpuSamplerDescription d) => inner.CreateSampler(d);
    public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d) => inner.CreateResourceLayout(d);
    public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d) => inner.CreateResourceSet(d);
    public IGpuShaderSet CreateShadersFromSpirv(string vertex, string fragment) => inner.CreateShadersFromSpirv(vertex, fragment);
    public IGpuShaderSet CreateShadersFromSpirv(string vertex, string fragment, GpuShaderCompileOptions options)
    {
        if (!options.PreciseMath) throw new InvalidOperationException("Coverage pulling requires precise vertex compilation.");
        return inner.CreateShadersFromSpirv(vertex, fragment);
    }
    public IGpuComputeShader CreateComputeShaderFromSpirv(string source) => CreateComputeShaderFromSpirv(source, default);
    public IGpuComputeShader CreateComputeShaderFromSpirv(string source, GpuShaderCompileOptions options)
    {
        if (source.Contains("CovStaticBuf", StringComparison.Ordinal))
        {
            firstCoverageSource ??= source;
            if (source == firstCoverageSource) CoverageBuildAttempts++;
            coverageShaderAttempts++;
            if (failCoverageBuild && coverageShaderAttempts == 3) throw new NotSupportedException("Injected coverage kernel build failure.");
            AssertPrecise(options);
        }
        var shader = new Shader();
        Shaders.Add(shader);
        return shader;
    }
    static void AssertPrecise(GpuShaderCompileOptions options)
    {
        if (!options.PreciseMath) throw new InvalidOperationException("Coverage requires precise compute compilation.");
    }
    public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d) => inner.CreateGraphicsPipeline(d);
    public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d) => new Pipeline();
    public IGpuCommandList CreateCommandList() => inner.CreateCommandList();
    public IGpuFence CreateFence() => inner.CreateFence();
    internal sealed class Shader : IGpuComputeShader
    {
        public uint ThreadGroupSizeX => 64;
        public uint ThreadGroupSizeY => 1;
        public uint ThreadGroupSizeZ => 1;
        internal bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
    sealed class Pipeline : IGpuComputePipeline { public void Dispose() { } }
}
