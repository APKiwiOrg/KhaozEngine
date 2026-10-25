using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Cold source identity for programs composed after the initialized shader fields.</summary>
internal static class RendererPerfShaderVariants
{
    internal static void AddTo(SortedDictionary<string, string> sources)
    {
        // The canonical shipped corpus owns lit and destination-preserving visibility variants,
        // both temporal precision families, and checked/certified programs. Apply the same backend
        // source policy as the actual pipeline factory, without creating a diagnostic device.
        foreach (var program in ShippedShaderPrograms.GraphicsPrograms())
        {
            sources.Add("shipped." + program.Name + ".vertex", program.VertexGlsl);
            foreach (var backend in new[] { GpuBackendKind.VulkanNative, GpuBackendKind.Direct3D11Native, GpuBackendKind.MetalNative })
            {
                sources.Add("shipped." + backend + "." + program.Name + ".fragment",
                    TemporalVisibilityGatherPolicy.ForBackend(program.FragmentGlsl, backend));
            }
        }
    }
}
