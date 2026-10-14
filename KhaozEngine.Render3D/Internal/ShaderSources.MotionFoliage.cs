namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// FoliageVert with motion (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 section 3): the same analytic placement, distance
/// fade, wind and interactor bend, evaluated twice, once with this frame's focus, clock, interactors and pixel scale and
/// once with last frame's. Both programs call the shared foliage displacement function, so
/// this frame's evaluation is the position FoliageVert would rasterise and last frame's is the one this program
/// rasterised then. The wind fade reads clip w only, which jitter does not move, so the previous evaluation takes the
/// unjittered previous matrix and the current one the frame block's matrix. Part of the <see cref="ShaderSources"/>
/// partial. It pairs with <see cref="ModelMotionFrag"/>.
/// </summary>
internal static partial class ShaderSources
{
    public const string FoliageMotionVert = @"#version 450
" + CompactFrameBlockGlsl + FoliageUniformBlockGlsl + @"layout(set=2, binding=0) uniform MotionFrame {" + MotionFrameMembersGlsl + @"};
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
layout(location=10) in vec4 FoliageMotion;
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
" + FoliageDisplacementGlsl + @"void main() {
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
