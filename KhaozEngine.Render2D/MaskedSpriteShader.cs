namespace KhaozEngine.Render2D
{
    /// <summary>The alpha-mask fragment variant paired with the ordinary SpriteBatch vertex shader.</summary>
    internal static class MaskedSpriteShader
    {
        internal const string FragmentSource = @"#version 450
layout(set=0, binding=0) uniform texture2D Tex;
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform texture2D Mask;
layout(set=0, binding=3) uniform sampler MaskSamp;
layout(location=0) in vec2 vUv;
layout(location=1) in vec4 vColor;
layout(location=2) in vec2 vLocal;
layout(location=0) out vec4 oColor;
void main() {
    vec4 base = texture(sampler2D(Tex, Samp), vUv) * vColor;
    base.a *= texture(sampler2D(Mask, MaskSamp), vLocal).a;
    oColor = base;
}";
    }
}
