namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfAdapterPolicy
{
    internal static bool Matches(RendererPerfExecutionClass executionClass, bool? software, string backend, uint vendor, uint device)
        => executionClass switch
        {
            // Vulkan sets SoftwareAdapter from the actual CPU class or retained Mesa llvmpipe identity.
            RendererPerfExecutionClass.Rehearsal => backend == "vulkan" && software == true,
            RendererPerfExecutionClass.Performance => software == false && vendor == 0x10de && device == 0x1eb8,
            _ => false
        };
}
