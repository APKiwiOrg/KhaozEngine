namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// The blocks every temporal variant shares (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 section 4). Part of the
/// <see cref="ShaderSources"/> partial, see ShaderSources.cs for the shared contract.
/// </summary>
internal static partial class ShaderSources
{
    /// <summary>The members of the per-frame <c>MotionFrame</c> block, in <see cref="Rendering.MotionFrameUbo"/> order.
    /// Both matrices are UNJITTERED and render-relative and are not clip-corrected, so motion is measured in the
    /// engine's own clip convention and V runs down the image on every backend. Each variant declares the block at its
    /// own first free set.</summary>
    public const string MotionFrameMembersGlsl = @"
    mat4 CurViewProj;     // this frame, unjittered, render-relative
    mat4 PrevViewProj;    // last frame, unjittered, rebased to this frame's render origin
    vec4 MotionParams;    // x = 1 when last frame is valid history, else 0
";

    /// <summary>The shared frame block <c>U</c> in FoliageVert's compact spelling: ModelVert's members in ModelVert's
    /// order, so a program declaring it agrees with <c>ModelRenderer.UboBytes</c>.
    /// <c>MotionUboLayoutTests.TheCompactFrameBlockDeclaresModelVertsMembersInOrder</c> holds the two together.</summary>
    public const string CompactFrameBlockGlsl = @"layout(set=0, binding=0) uniform U {
    mat4 ViewProj;
    vec4 LightDir; vec4 LightColor; vec4 Ambient; vec4 Params;
    vec4 FillDir; vec4 FillColor; vec4 CameraPos;
    vec4 PointPosRadius[16]; vec4 PointColorIntensity[16];
    mat4 ShadowMat[4]; vec4 ShadowParams; vec4 ShadowParams2;
    vec4 ShadowNormalOffsets; vec4 RenderOrigin;
    vec4 PointShadowParams[16]; vec4 PointShadowAtlas; vec4 PointShadowFilter; vec4 PointShadowTransientAtlas;
    vec4 ClusterDepth; vec4 ClusterCamera;
};
";
}
