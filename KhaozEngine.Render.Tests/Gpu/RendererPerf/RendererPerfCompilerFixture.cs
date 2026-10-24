using System.Collections.Immutable;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Synthetic codec contracts only. Production driver never consumes this fixture.
internal static class RendererPerfCompilerFixture
{
    internal static GpuPipelineConstructionIdentity Identity(string hash)
    {
        var source = GpuDiagnosticIdentityEncoding.Known(hash, "glsl.utf8/1");
        var artifact = GpuDiagnosticIdentityEncoding.Known(hash, "spirv.bytes/1");
        return new(GpuDiagnosticIdentityEncoding.Known(hash, "vulkan.pipeline/1"), source, artifact,
            GpuDiagnosticIdentityEncoding.Known(hash, "vulkan.layout/1"), GpuDiagnosticIdentityEncoding.Configuration(Configuration),
            GpuDiagnosticIdentityEncoding.CodeGeneration(GpuBackendKind.VulkanNative, []), "synthetic optimized", 1, 1, 1,
            [new("vertex", "vs_main", source, null, artifact)]);
    }

    internal static GpuEffectiveDiagnosticConfiguration Configuration { get; } = new(GpuBackendKind.VulkanNative,
        "synthetic-adapter", "synthetic-driver", new(false, false), false, false, false, false, true, false, 0, [], []);

    internal static RendererPerfCompilerEvidence Complete { get; } = new(RendererPerfCollectionState.Complete, null,
        new(1, new(1, 0), new(1, 0), new(1, 0), new(1, 0), new(1, 0), 1, 1, 1, 1, 1, 0), Configuration, Configuration,
        [new(0, RendererPerfCollectionState.Complete, null, 0, [0], new(new(64, 256, 16384, 256, 16384, 4),
            new(true, 1, 1, 1, 1, 0, 0, true, null, null), true, null))], "synthetic-inventory.json", [], []);
}
