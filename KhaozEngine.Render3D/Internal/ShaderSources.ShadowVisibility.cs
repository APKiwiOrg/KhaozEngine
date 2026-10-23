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
float pcfCascade(texture2D shadowAtlas, sampler shadowSamp, int cascade, int count, vec2 uv, float depth, float texelStep) {
    float atlasScaleX = 1.0 / float(count);
    float atlasBiasX = float(cascade) * atlasScaleX;
    float last = 1.0 / texelStep - 1.0;
    vec2 texel = uv / texelStep - vec2(0.5);
    vec2 base = floor(texel);
    vec2 f = texel - base;
    vec4 wx = vec4(1.0 - f.x, 1.0, 1.0, f.x);
    vec4 wy = vec4(1.0 - f.y, 1.0, 1.0, f.y);
    vec2 origin = clamp(base - vec2(1.0), vec2(0.0), vec2(last - 3.0));
    vec4 ex = vec4(0.0);
    vec4 ey = vec4(0.0);
    for (int k = 0; k < 4; k++) {
        vec2 j = clamp(base + vec2(float(k - 1)), vec2(0.0), vec2(last)) - origin;
        ex += wx[k] * vec4(equal(vec4(j.x), vec4(0.0, 1.0, 2.0, 3.0)));
        ey += wy[k] * vec4(equal(vec4(j.y), vec4(0.0, 1.0, 2.0, 3.0)));
    }
    float lit = 0.0;
    for (int gy = 0; gy < 2; gy++) {
        for (int gx = 0; gx < 2; gx++) {
            vec2 luv = (origin + vec2(float(2 * gx + 1), float(2 * gy + 1))) * texelStep;
            vec2 auv = vec2(luv.x * atlasScaleX + atlasBiasX, luv.y);
            vec4 lit4 = vec4(lessThanEqual(vec4(depth), textureGather(sampler2D(shadowAtlas, shadowSamp), auv, 0)));
            vec2 x01 = gx == 0 ? ex.xy : ex.zw;
            vec2 y01 = gy == 0 ? ey.xy : ey.zw;
            lit += dot(lit4, vec4(x01.x * y01.y, x01.y * y01.y, x01.y * y01.x, x01.x * y01.x));
        }
    }
    return lit / 9.0;
}
";
    }
}
