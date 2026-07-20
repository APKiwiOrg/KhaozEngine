namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// GLSL #version 450 shader sources, cross-compiled at load via the GPU seam's SPIR-V path
    /// (GLSL -> SPIR-V -> MSL/HLSL/GLSL). The model and post shaders use the separate texture2D + sampler style
    /// (not combined sampler2D) so the
    /// ResourceLayout binding order is unambiguous. The model pass writes 3 MRT color targets
    /// (lit color, encoded normal, linear-ish depth) so the edge pass never samples a depth texture.
    ///
    /// The sources are split across ShaderSources.&lt;Domain&gt;.cs partial files by render domain, so the
    /// file-size ratchet reports growth per domain rather than on one 2600-line pile. Splitting is by
    /// responsibility, not by line count: every member below is a compile-time const, so
    /// const-concatenation across the partials (ModelFrag splicing in LightingCommonGlsl, for one)
    /// still resolves at compile time exactly as it did when they shared a file.
    /// </summary>
    internal static partial class ShaderSources
    {
    }
}
