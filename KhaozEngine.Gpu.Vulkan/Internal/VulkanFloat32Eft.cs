namespace KhaozEngine.Gpu.Vulkan.Internal;

/// <summary>Refuse an EFT module before native creation unless every declared float control is supported.</summary>
internal static class VulkanFloat32Eft
{
    internal static void Require(GpuCapabilities capabilities, GpuShaderCompileOptions options)
    {
        if (!options.RequireFloat32Eft) return;
        if (!options.PreciseMath || !capabilities.SupportsShaderFloatControls || !capabilities.SupportsFloat32Rte
            || !(options.FlushFloat32Denorms ? capabilities.SupportsFloat32DenormFlush : capabilities.SupportsFloat32DenormPreserve))
            throw new ShaderValidationException("Float32 EFT requires precise math, shaderSignedZeroInfNanPreserveFloat32, "
                + "shaderRoundingModeRTEFloat32 and the requested denormal mode.");
    }
}
