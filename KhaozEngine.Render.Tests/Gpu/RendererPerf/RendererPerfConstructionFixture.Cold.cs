using System;
using System.Collections.Immutable;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed partial class RendererPerfConstructionFixture : IGpuDeviceDiagnosticConfiguration,
    IGpuDeviceLifecycle, IGpuResourceMemoryDiagnostics
{
    internal Action<string>? ColdAction;
    internal string ArtifactSalt = "";
    internal IVulkanPipelineExecutableApi? CompilerApi;
    internal GpuPipelineCompilerReadResult? LastRead;
    internal System.Collections.Generic.List<KhaozEngine.Tests.Gpu.Performance.GpuPerfTimestampTestPool> Pools => _inner.Pools;
    internal GpuBackendKind ActualBackend = GpuBackendKind.VulkanNative;
    internal GpuEffectiveDiagnosticConfiguration Configuration = Production;
    internal readonly D3D11MemoryDiagnostics Memory = new();
    public GpuEffectiveDiagnosticConfiguration DiagnosticConfiguration
    { get { ColdAction?.Invoke("configuration"); return Configuration; } }
    public GpuDeviceCounters Counters { get { ColdAction?.Invoke("counters"); return default; } }
    public void MarkDeviceDisposed() => ColdAction?.Invoke("context.mark");
    public GpuResourceMemorySnapshot ReadMemorySnapshot()
    { ColdAction?.Invoke("memory"); return Memory.ReadMemorySnapshot(); }
    GpuPipelineCompilerReadResult ColdRead(GpuPipelineConstructionIdentity witness, GpuPipelineCompilerReadRequest request,
        Action<string> check, CancellationToken cancellation)
    {
        CompilerReads++;
        ColdAction?.Invoke("read");
        var identity = new VulkanPipelineIdentity(witness.PipelineSha256.Sha256!, witness.SourceSha256.Sha256!,
            witness.ShaderArtifactSha256.Sha256!, witness.LayoutSha256.Sha256!, witness.CompilerFlags,
            witness.LocalSizeX, witness.LocalSizeY, witness.LocalSizeZ)
        { Stages = witness.Stages, CodeGeneration = witness.CodeGenerationSha256 };
        return LastRead = new VulkanPipelineDiagnostics(true, Configuration.PipelineStatisticsSupported,
            Configuration.PipelineExecutableFeatureEnabled, CompilerApi, "injected native operations", effective: Configuration)
            .Read(1, (PipelineCreateFlags)Configuration.PipelineCaptureFlags, identity, request, check, cancellation, witness);
    }
}
