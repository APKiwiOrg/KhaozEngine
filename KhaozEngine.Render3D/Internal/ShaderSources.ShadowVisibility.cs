namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The key light's cascade comparison filter, spliced into <see cref="LightingCommonGlsl"/> ahead of
    /// <c>sampleKeyShadow</c>. Part of the <see cref="ShaderSources"/> partial: see ShaderSources.cs for the shared
    /// contract. It references only its parameters, so a test can splice it into a kernel of its own and read the
    /// filter back without the frame uniforms the rest of the lighting block needs.
    /// </summary>
    internal static partial class ShaderSources
    {
        public const string CascadeShadowFilterGlsl = @"
// One cascade's bilinear 3x3 comparison PCF inside its atlas column. uv is cascade-local, depth is already biased.
// Nine comparison taps at whole-texel offsets {-1,0,1}, each the bilinear interpolation of four POINT comparisons, so
// the result moves continuously with the receiver instead of stepping a ninth at each texel crossing. The nine taps
// share their inner texels, which folds them into one 4x4 footprint weighted [1-f, 1, 1, f] on each axis, sixteen
// comparisons divided by nine. On a texel centre f is zero and this is the plain 3x3 point kernel.
// COMPARE FIRST, FILTER AFTER. Each texel fetches ONE stored depth (the atlas sampler is POINT, see ShadowMapRenderer's
// ctor), compares it, and only the 0/1 comparison RESULTS are weighted. Interpolating stored depths instead would
// blend the atlas clear value (1.0 = no caster) into every tap next to a gap, which can only ever lighten the result,
// and erases a dithered caster's shadow outright outside cascade 0 (issue #391). Every texel centre is CLAMPED inside
// the column, then mapped to atlas U, so it never bleeds into a neighbour cascade. The explicit LOD keeps the fetch
// defined in the hand-off branch, where implicit derivatives are not, and the atlas has a single level.
float pcfCascade(texture2D shadowAtlas, sampler shadowSamp, int cascade, int count, vec2 uv, float depth, float texelStep) {
    float atlasScaleX = 1.0 / float(count);
    float atlasBiasX = float(cascade) * atlasScaleX;
    float halfTexel = texelStep * 0.5;
    vec2 texel = uv / texelStep - vec2(0.5);            // texel space, integers on texel centres
    vec2 base = floor(texel);
    vec2 f = texel - base;
    vec4 wx = vec4(1.0 - f.x, 1.0, 1.0, f.x);
    vec4 wy = vec4(1.0 - f.y, 1.0, 1.0, f.y);
    float lit = 0.0;
    for (int y = 0; y < 4; y++) {
        for (int x = 0; x < 4; x++) {
            // The centre of texel base + (x - 1, y - 1).
            vec2 luv = (base + vec2(float(x), float(y)) - vec2(0.5)) * texelStep;
            luv = clamp(luv, vec2(halfTexel), vec2(1.0 - halfTexel));
            vec2 auv = vec2(luv.x * atlasScaleX + atlasBiasX, luv.y);
            float d = textureLod(sampler2D(shadowAtlas, shadowSamp), auv, 0.0).r;
            lit += ((depth <= d) ? 1.0 : 0.0) * wx[x] * wy[y];   // receiver in front of the stored caster depth => lit
        }
    }
    return lit / 9.0;
}
";
    }
}
