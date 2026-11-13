namespace KhaozEngine.Render3D.Internal
{
    /// <summary>The material texture LOD snippets. Part of the <see cref="ShaderSources"/> partial.</summary>
    internal static partial class ShaderSources
    {
        // ---- Material texture LOD under temporal upscaling (TEMPORAL-RESOLVE section 5). Spliced
        //      into every fragment that samples a material, after the shared frame block, whose Params.z carries
        //      log2(internal / display) + Post.Temporal.MipBiasOffset while the temporal resolve runs and whose
        //      Params.w carries exp2(Params.z) - 1 (TemporalMipBias). Both are exact zeros otherwise, so the gradient
        //      scale is exactly 1.0 and moves nothing. Every material tap scales both derivatives by exp2(bias), which
        //      moves the isotropic and the anisotropic LOD by exactly the bias on every backend, whatever rule a
        //      backend uses for a bias argument. The bias is never sampler state, because Metal samplers carry no LOD
        //      bias. It is never a texture(s, uv, bias) argument either, because on Apple GPUs that argument through an
        //      anisotropic sampler does not shift the LOD as it does through a trilinear one: a dark model texture at
        //      Quality measured 18 percent over its error baseline through bias() at 16x and exactly on it through
        //      scaled gradients (#1776). The model taps derive their gradients from
        //      the UV inside materialSample, and the ground taps hoist world-space ones scaled by materialGradScale.
        //      MaterialMipBiasShaderContractTests fails any material program that samples around these. ----
        public const string MaterialLodSampleGlsl = @"
vec4 materialSample(texture2D map, sampler samp, vec2 uv)
{
    float g = 1.0 + Params.w;
    return textureGrad(sampler2D(map, samp), uv, dFdx(uv) * g, dFdy(uv) * g);
}
";

        public const string MaterialLodGradGlsl = @"
float materialGradScale() { return 1.0 + Params.w; }
";
    }
}
