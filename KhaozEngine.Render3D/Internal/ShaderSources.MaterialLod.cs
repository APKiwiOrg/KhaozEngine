namespace KhaozEngine.Render3D.Internal
{
    /// <summary>The material texture LOD snippets. Part of the <see cref="ShaderSources"/> partial.</summary>
    internal static partial class ShaderSources
    {
        // ---- Material texture LOD under temporal upscaling (TEMPORAL-RESOLVE-UPSCALING-DESIGN section 5). Spliced
        //      into every fragment that samples a material, after the shared frame block, whose Params.z carries
        //      log2(internal / display) + Post.Temporal.MipBiasOffset while the temporal resolve runs and whose
        //      Params.w carries exp2(Params.z) - 1 (TemporalMipBias). Both are exact zeros otherwise, so every tap
        //      is the unbiased tap: a bias of 0.0 adds nothing to the hardware LOD and a gradient scale of exactly
        //      1.0 moves nothing. The bias is a shader argument rather than sampler state because Metal samplers
        //      carry no LOD bias. texture(s, uv, bias) cross-compiles to an MSL bias() argument, to HLSL SampleBias
        //      and to the SPIR-V Bias operand. The explicit-gradient ground taps cannot take a bias (textureGrad
        //      has none), so they scale both derivatives by exp2(bias), which moves the isotropic and the
        //      anisotropic LOD by exactly the bias. MaterialMipBiasShaderContractTests fails any material program
        //      that samples around these. ----
        public const string MaterialLodSampleGlsl = @"
vec4 materialSample(texture2D map, sampler samp, vec2 uv) { return texture(sampler2D(map, samp), uv, Params.z); }
";

        public const string MaterialLodGradGlsl = @"
float materialGradScale() { return 1.0 + Params.w; }
";
    }
}
