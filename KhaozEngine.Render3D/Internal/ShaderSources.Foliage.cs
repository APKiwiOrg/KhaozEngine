namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // Immutable instance matrices and ranks stay on the GPU. Motion is rooted in the authored mesh.
    public const string FoliageVert = @"#version 450
layout(set=0, binding=0) uniform U {
    mat4 ViewProj;
    vec4 LightDir; vec4 LightColor; vec4 Ambient; vec4 Params;
    vec4 FillDir; vec4 FillColor; vec4 CameraPos;
    vec4 PointPosRadius[16]; vec4 PointColorIntensity[16];
    mat4 ShadowMat[4]; vec4 ShadowParams; vec4 ShadowParams2;
    vec4 ShadowNormalOffsets; vec4 RenderOrigin;
};
layout(set=1, binding=0) uniform Foliage {
    vec4 FocusRadius;
    vec4 Density;
    vec4 FadeWind;
    vec4 WindTime;
    vec4 Interactors[4];
    vec4 Strengths;
};
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=4) in vec4 Tangent;
layout(location=5) in vec4 IModel0;
layout(location=6) in vec4 IModel1;
layout(location=7) in vec4 IModel2;
layout(location=8) in vec4 IModel3;
layout(location=9) in vec4 FoliageParameters;
layout(location=0) out vec3 vNormalW;
layout(location=1) out vec4 vColor;
layout(location=2) out vec3 vWorldPos;
layout(location=3) out vec2 vUv;
layout(location=4) out vec4 vTint;
layout(location=5) out vec4 vEmissive;
layout(location=6) out vec4 vSpecParams;
layout(location=7) out vec4 vTangent;
layout(location=8) out float vDynamic;
layout(location=9) out vec2 vDissolve;
void main() {
    float rank = FoliageParameters.x;
    float distanceToFocus = length(IModel3.xz - FocusRadius.xz);
    float cutoff = FocusRadius.w;
    float fadeStart = max(0.0, cutoff - WindTime.w);
    float inner = Density.z - Density.w;
    if (Density.x > Density.y && rank >= Density.y) {
        cutoff = inner + (Density.x - rank) / (Density.x - Density.y) * Density.w;
        fadeStart = max(inner, cutoff - min(FadeWind.x, Density.w));
    }
    float fade = cutoff > fadeStart ? clamp((distanceToFocus - fadeStart) / (cutoff - fadeStart), 0.0, 1.0) : 0.0;
    float heightFade = 1.0 - fade * fade * (3.0 - 2.0 * fade);
    bool rejected = rank >= Density.x || distanceToFocus > cutoff || heightFade <= 0.0001;
    mat4 Model = mat4(IModel0, IModel1, IModel2, IModel3);
    Model[3].xyz -= RenderOrigin.xyz;
    Model[1].xyz *= max(heightFade, 0.00001);
    vec4 world = Model * vec4(Position, 1.0);
    float invHeight = FoliageParameters.z;
    float bladeHeight = invHeight > 0.0 ? length(IModel1.xyz) / invHeight * heightFade : 0.0;
    float rootWeight = clamp((Position.y - FoliageParameters.y) * invHeight, 0.0, 1.0);
    rootWeight *= rootWeight;
    float phase = dot(IModel3.xz, WindTime.xy) * FadeWind.w - WindTime.z * FadeWind.z;
    vec2 bend = WindTime.xy * (sin(phase) * 0.7 + sin(phase * 0.43 + 1.7) * 0.3) * FadeWind.y * bladeHeight;
    vec3 root = (Model * vec4(0.0, FoliageParameters.y, 0.0, 1.0)).xyz;
    for (int i = 0; i < 4; i++) {
        if (Interactors[i].w <= 0.0 || Strengths[i] <= 0.0) continue;
        vec3 delta = root - (Interactors[i].xyz - RenderOrigin.xyz);
        float falloff = 1.0 - smoothstep(0.0, Interactors[i].w, length(delta));
        bend += delta.xz / max(length(delta.xz), 0.05) * falloff * Strengths[i] * bladeHeight;
    }
    float bendLength = length(bend);
    if (bendLength > bladeHeight * 0.65 && bendLength > 0.0) bend *= bladeHeight * 0.65 / bendLength;
    world.xz += bend * rootWeight;
    world.y -= (bladeHeight - sqrt(max(0.0, bladeHeight * bladeHeight - dot(bend, bend)))) * rootWeight;
    gl_Position = rejected ? vec4(2.0, 2.0, 2.0, 1.0) : ViewProj * world;
    vNormalW = normalize(mat3(Model) * Normal);
    vColor = Color;
    vWorldPos = world.xyz;
    vUv = TexCoord;
    vTint = vec4(1.0);
    vEmissive = vec4(0.0);
    vSpecParams = vec4(0.0, 32.0, FoliageParameters.w, 0.0);
    vTangent = vec4(mat3(Model) * Tangent.xyz, Tangent.w);
    vDynamic = 0.0;
    vDissolve = vec2(0.0);
}";
}
