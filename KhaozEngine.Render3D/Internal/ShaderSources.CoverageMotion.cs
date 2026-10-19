namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    internal static string CoverageModelMotionVert => ShaderText.BeforeEndOfMain(
        ShaderText.After(CoverageModelVert, "layout(location=9) out vec3 vDissolve;", @"
layout(set=3, binding=0) uniform MotionFrame {" + MotionFrameMembersGlsl + @"};
layout(location=10) out vec4 vCurClip;
layout(location=11) out vec4 vPrevClip;"), @"
    mat4 previousModel = coverageSeedPrevious() ? Model : coveragePreviousModel;
    vec4 previousWorld = coverageTransform(previousModel, vec4(coveragePreviousPosition(), 1.0));
    vCurClip = coverageTransform(CurViewProj, world);
    vPrevClip = MotionParams.x > 0.5 ? coverageTransform(PrevViewProj, previousWorld) : vCurClip;
");
}
