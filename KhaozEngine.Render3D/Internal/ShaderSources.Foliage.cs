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
    // The point-shadow tail. This stage reads none of it, but the foliage PROGRAM is FoliageVert plus ModelFrag,
    // so the two stages share this block and a vertex declaring it 272 bytes short is the two disagreeing about
    // the size of one buffer. Same trap the render origin left when it was appended.
    vec4 PointShadowParams[16]; vec4 PointShadowAtlas; vec4 PointShadowFilter; vec4 PointShadowTransientAtlas;
    vec4 ClusterDepth; vec4 ClusterCamera;
    vec4 InteriorCutWindow; vec4 InteriorCutHeights;
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
" + FoliageDisplacementGlsl + @"void main() {
    mat4 Model;
    bool rejected;
    vec4 world = vec4(foliageWorld(FocusRadius.xyz, WindTime.z, WindFade.y, ViewProj, false, Model, rejected), 1.0);
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
}";
}
