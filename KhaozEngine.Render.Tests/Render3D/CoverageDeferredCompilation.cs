using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Render3D;

internal abstract class CoverageDeferredCompilation(IDisposable inner, bool fail) : IDisposable
{
    readonly ShaderValidationException? failure = fail ? new("Synthetic deferred coverage compilation refusal.") : null;
    internal bool Fails => failure is not null;
    internal bool Disposed { get; private set; }
    internal bool NativeConsumed { get; private set; }
    public void EnsureCompilationReady()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (failure is not null) throw failure;
    }
    internal void Bind() { EnsureCompilationReady(); NativeConsumed = true; }
    public void Dispose() { Disposed = true; inner.Dispose(); }
}

internal sealed class CoverageDeferredShaderSet(IGpuShaderSet inner, bool fail)
    : CoverageDeferredCompilation(inner, fail), IGpuShaderSet
{
    internal IGpuShaderSet Inner => inner;
}

internal sealed class CoverageDeferredComputeShader(IGpuComputeShader inner, bool fail)
    : CoverageDeferredCompilation(inner, fail), IGpuComputeShader
{
    internal IGpuComputeShader Inner => inner;
    public uint ThreadGroupSizeX { get { EnsureCompilationReady(); return inner.ThreadGroupSizeX; } }
    public uint ThreadGroupSizeY { get { EnsureCompilationReady(); return inner.ThreadGroupSizeY; } }
    public uint ThreadGroupSizeZ { get { EnsureCompilationReady(); return inner.ThreadGroupSizeZ; } }
}

internal interface ICoverageDeferredPipeline { void Bind(); }
internal sealed class CoverageDeferredGraphicsPipeline(IGpuPipeline inner, CoverageDeferredShaderSet shader)
    : IGpuPipeline, ICoverageDeferredPipeline
{
    public void Bind() => shader.Bind();
    public void Dispose() => inner.Dispose();
}
internal sealed class CoverageDeferredComputePipeline(IGpuComputePipeline inner, CoverageDeferredComputeShader shader)
    : IGpuComputePipeline, ICoverageDeferredPipeline
{
    internal IGpuComputePipeline Inner => inner;
    public void Bind() => shader.Bind();
    public void Dispose() => inner.Dispose();
}
