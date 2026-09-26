namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// FoliageVert with motion (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 section 3): the same analytic placement, distance
/// fade, wind and interactor bend, evaluated twice, once with this frame's focus, clock, interactors and pixel scale and
/// once with last frame's. The deformation is FoliageVert's statements in FoliageVert's order moved into a function, so
/// this frame's evaluation is the position FoliageVert would rasterise and last frame's is the one this program
/// rasterised then. The wind fade reads clip w only, which jitter does not move, so the previous evaluation takes the
/// unjittered previous matrix and the current one the frame block's matrix. Part of the <see cref="ShaderSources"/>
/// partial. It pairs with <see cref="ModelMotionFrag"/>.
/// </summary>
internal static partial class ShaderSources
{
    public const string FoliageMotionVert = @"#version 450
" + CompactFrameBlockGlsl + @"layout(set=1, binding=0) uniform Foliage {
    vec4 FocusRadius;
    vec4 Density;
    vec4 FadeWind;
    vec4 WindTime;
    vec4 Interactors[4];
    vec4 Strengths;
    vec4 WindFade;          // x = blade pixels where wind stops, y = metres per pixel now, z = the same last frame
    vec4 PrevFocus;         // xyz = last frame's focus, w = last frame's wind time
    vec4 PrevInteractors[4];
    vec4 PrevStrengths;
};
layout(set=2, binding=0) uniform MotionFrame {" + MotionFrameMembersGlsl + @"};
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
layout(location=10) out float vDissolveComplement;
layout(location=11) out vec4 vCurClip;
layout(location=12) out vec4 vPrevClip;
vec3 foliageWorld(vec3 focus, float windTime, float metresPerPixel, mat4 fadeViewProj, bool previous,
                  out mat4 Model, out bool rejected) {
    float rank = FoliageParameters.x;
    float distanceToFocus = length(IModel3.xz - focus.xz);
    float cutoff = FocusRadius.w;
    float fadeStart = max(0.0, cutoff - WindTime.w);
    float inner = Density.z - Density.w;
    if (Density.x > Density.y && rank >= Density.y) {
        cutoff = inner + (Density.x - rank) / (Density.x - Density.y) * Density.w;
        fadeStart = max(inner, cutoff - min(FadeWind.x, Density.w));
    }
    float fade = cutoff > fadeStart ? clamp((distanceToFocus - fadeStart) / (cutoff - fadeStart), 0.0, 1.0) : 0.0;
    float heightFade = 1.0 - fade * fade * (3.0 - 2.0 * fade);
    rejected = rank >= Density.x || distanceToFocus > cutoff || heightFade <= 0.0001;
    Model = mat4(IModel0, IModel1, IModel2, IModel3);
    Model[3].xyz -= RenderOrigin.xyz;
    Model[3].xyz += IModel1.xyz * FoliageParameters.y * (1.0 - heightFade);
    Model[1].xyz *= max(heightFade, 0.00001);
    vec4 world = Model * vec4(Position, 1.0);
    float invHeight = FoliageParameters.z;
    float bladeHeight = invHeight > 0.0 ? length(IModel1.xyz) / invHeight * heightFade : 0.0;
    float rootWeight = clamp((Position.y - FoliageParameters.y) * invHeight, 0.0, 1.0);
    rootWeight *= rootWeight;
    float phase = dot(IModel3.xz, WindTime.xy) * FadeWind.w - windTime * FadeWind.z;
    vec3 root = (Model * vec4(0.0, FoliageParameters.y, 0.0, 1.0)).xyz;
    vec2 bend = WindTime.xy * (sin(phase) * 0.7 + sin(phase * 0.43 + 1.7) * 0.3) * FadeWind.y * bladeHeight;
    if (WindFade.x > 0.0) {
        float fadeHeight = max((fadeViewProj * vec4(root, 1.0)).w, 0.0) * metresPerPixel * WindFade.x;
        bend *= fadeHeight > 0.0 ? smoothstep(1.0, 2.0, bladeHeight / fadeHeight) : 1.0;
    }
    for (int i = 0; i < 4; i++) {
        vec4 interactor = previous ? PrevInteractors[i] : Interactors[i];
        float strength = previous ? PrevStrengths[i] : Strengths[i];
        if (interactor.w <= 0.0 || strength <= 0.0) continue;
        vec3 delta = root - (interactor.xyz - RenderOrigin.xyz);
        float falloff = 1.0 - smoothstep(0.0, interactor.w, length(delta));
        bend += delta.xz / max(length(delta.xz), 0.05) * falloff * strength * bladeHeight;
    }
    float bendLength = length(bend);
    if (bendLength > bladeHeight * 0.65 && bendLength > 0.0) bend *= bladeHeight * 0.65 / bendLength;
    world.xz += bend * rootWeight;
    world.y -= (bladeHeight - sqrt(max(0.0, bladeHeight * bladeHeight - dot(bend, bend)))) * rootWeight;
    return world.xyz;
}
void main() {
    mat4 Model;
    bool rejected;
    vec4 world = vec4(foliageWorld(FocusRadius.xyz, WindTime.z, WindFade.y, ViewProj, false, Model, rejected), 1.0);
    mat4 lastModel;
    bool lastRejected;
    vec3 lastWorld = foliageWorld(PrevFocus.xyz, PrevFocus.w, WindFade.z, PrevViewProj, true, lastModel, lastRejected);
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
    vDissolveComplement = 0.0;
    vCurClip = CurViewProj * world;
    vPrevClip = MotionParams.x > 0.5 ? PrevViewProj * vec4(lastWorld, 1.0) : vCurClip;
}";
}
