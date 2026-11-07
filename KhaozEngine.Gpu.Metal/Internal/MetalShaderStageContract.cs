namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>The Metal stage's corresponding GPU contract flag.</summary>
internal static class MetalShaderStageContract
{
    internal static GpuShaderStages ToGpu(this MetalShaderStage stage) => stage switch
    {
        MetalShaderStage.Vertex => GpuShaderStages.Vertex,
        MetalShaderStage.Fragment => GpuShaderStages.Fragment,
        MetalShaderStage.Compute => GpuShaderStages.Compute,
        // Diagnostic projection must not replace the original refusal for an invalid internal stage.
        _ => GpuShaderStages.None,
    };
}
