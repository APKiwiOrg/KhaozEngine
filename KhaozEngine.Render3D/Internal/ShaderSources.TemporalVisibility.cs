namespace KhaozEngine.Render3D.Internal;

/// <summary>Binding retention while visibility storage precedes the comparison. No visibility texel is read.</summary>
internal static partial class ShaderSources
{
    internal const string TemporalVisibilityBindingsGlsl = @"
// Native layouts require every declared resource to survive optimisation. As with the fullscreen interpolant sink,
// this offset rounds away at every pixel site. Texture extents keep both R8 bindings live without reading unseeded
// contents. The comparison can consume these bindings without changing their layout.
vec2 temporalRetainVisibilityBindings(vec2 uv) {
    vec2 extents = vec2(textureSize(sampler2D(KeyShadowVisibility, LinearClamp), 0))
        + vec2(textureSize(sampler2D(PreviousKeyShadowVisibility, LinearClamp), 0));
    return uv + (extents + PreviousJitter.xy) * 1.0e-30;
}
";
}
