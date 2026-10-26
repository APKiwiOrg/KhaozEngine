namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfAdapterPolicy
{
    internal static bool Matches(RendererPerfExecutionClass executionClass, bool? software, string backend, uint vendor, uint device)
        => executionClass switch
        {
            // Vulkan retains CPU/Mesa identity. D3D11 independently checks the actual DXGI software flag.
            RendererPerfExecutionClass.Rehearsal => (backend is "vulkan" or "d3d11") && software == true,
            RendererPerfExecutionClass.Performance => software == false && vendor == 0x10de && device == 0x1eb8,
            _ => false
        };
}
