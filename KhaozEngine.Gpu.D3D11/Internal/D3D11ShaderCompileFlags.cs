namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>Stage-specific FXC arithmetic policy, shared by compilation and its cache identity.</summary>
    internal static class D3D11ShaderCompileFlags
    {
        internal static uint For(D3D11ShaderStage stage, uint requested, GpuShaderCompileOptions options)
            => options.PreciseMath && stage is D3D11ShaderStage.Vertex or D3D11ShaderStage.Compute
                ? requested | (uint)Vortice.D3DCompiler.ShaderFlags.IeeeStrictness
                : requested;
    }
}
