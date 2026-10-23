namespace KhaozEngine.Gpu
{
    /// <summary>Per-shader compiler policy. Default preserves the usual optimized compilation.</summary>
    /// <param name="PreciseMath">Preserve authored GLSL <c>precise</c> arithmetic in compute or vertex shaders.
    /// This controls compiler policy only: shaderc optimization Zero, Metal fast math off for that stage,
    /// FXC IEEE strictness and a distinct cache identity. It never injects <c>NoContraction</c> decorations,
    /// which come from authored GLSL <c>precise</c> arithmetic. Coverage-style kernels must author
    /// <c>precise</c> on the arithmetic that matters and compile with <c>PreciseMath</c> enabled.
    /// It does not promise identical transcendental or denormal behavior across devices.</param>
    /// <param name="RequireFloat32Eft">Require Vulkan RTE32 and an explicit denormal mode for compute error-free transforms.
    /// Requires PreciseMath and the queried RTE and selected denormal capabilities.</param>
    /// <param name="FlushFloat32Denorms">Select supported explicit FTZ instead of preservation for a guarded EFT.
    /// Every nonzero EFT intermediate must stay normal when this is selected.</param>
    public readonly record struct GpuShaderCompileOptions(bool PreciseMath = false, bool RequireFloat32Eft = false,
        bool FlushFloat32Denorms = false);
}
