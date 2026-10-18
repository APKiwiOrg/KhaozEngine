namespace KhaozEngine.Gpu
{
    /// <summary>Per-shader compiler policy. Default preserves the usual optimized compilation.</summary>
    /// <param name="PreciseMath">Preserve authored GLSL <c>precise</c> arithmetic in compute or vertex shaders.
    /// This disables unsafe contraction and fast math for that stage. It does not insert source qualifiers or
    /// promise identical transcendental or denormal behavior across devices.</param>
    public readonly record struct GpuShaderCompileOptions(bool PreciseMath = false);
}
