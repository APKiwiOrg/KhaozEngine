namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    public const string FoliageDepthVert = @"#version 450
layout(set=0, binding=0) uniform U {
    mat4 LightViewProj;
    mat4 CameraViewProj;
    vec4 RenderOrigin;
};
" + FoliageUniformBlockGlsl + @"layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=4) in vec4 Tangent;
layout(location=5) in vec4 IModel0;
layout(location=6) in vec4 IModel1;
layout(location=7) in vec4 IModel2;
layout(location=8) in vec4 IModel3;
layout(location=9) in vec4 FoliageParameters;
layout(location=10) in vec4 FoliageMotion;
layout(location=0) out float vLightDepth;
layout(location=1) out vec2 vUv;
layout(location=2) out float vCutoff;
" + FoliageDisplacementGlsl + @"void main() {
    mat4 Model;
    bool rejected;
    vec4 world = vec4(foliageWorld(FocusRadius.xyz, WindTime.z, WindFade.y, CameraViewProj, false, Model, rejected), 1.0);
    // Keep every model vertex input live, including UV on the opaque pair, for contiguous FXC signatures.
    float sink = Normal.x + Color.x + TexCoord.x + Tangent.x;
    world.x += sink * 1e-30;
    vec4 lightClip = LightViewProj * world;
    vLightDepth = lightClip.z / lightClip.w;
    lightClip.z = max(lightClip.z, 0.0);
    gl_Position = rejected ? vec4(2.0, 2.0, 2.0, 1.0) : lightClip;
    vUv = TexCoord;
    vCutoff = FoliageParameters.w;
}";

    public const string FoliageDepthCutoutFrag = @"#version 450
layout(set=2, binding=0) uniform texture2D Albedo;
layout(set=2, binding=1) uniform sampler Samp;
layout(location=0) in float vLightDepth;
layout(location=1) in vec2 vUv;
layout(location=2) in float vCutoff;
layout(location=0) out vec4 oDepth;
void main() {
    if (vCutoff > 0.0 && texture(sampler2D(Albedo, Samp), vUv).a < vCutoff) discard;
    oDepth = vec4(max(vLightDepth, 0.0), 0.0, 0.0, 1.0);
}";
}
