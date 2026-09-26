namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// The temporal variants of the model-pass vertex programs (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 section 3). Each is its
/// base vertex byte for byte, plus the motion declarations after its last output and the two clip positions at the end
/// of main. Both clip positions come from UNJITTERED matrices in the <c>MotionFrame</c> block while <c>gl_Position</c>
/// keeps the frame block's jittered matrix, so jitter never reads as motion. When the block says there is no valid
/// history, <c>vPrevClip</c> is <c>vCurClip</c> itself and the fragment writes exactly zero. Part of the
/// <see cref="ShaderSources"/> partial.
/// </summary>
internal static partial class ShaderSources
{
    /// <summary>ModelVert with motion, the rigid instanced path. An instance's previous world transform is
    /// <c>PrevModel[IMotionSlot]</c>, or its own transform when the slot is -1 (unkeyed, or keyed with no last frame),
    /// which is camera-only motion. Set 1 carries the motion block and the previous transforms. Location 15 is the one
    /// attribute Vulkan guarantees above the instance stream's 5 to 14 (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24
    /// section 3).</summary>
    public static readonly string ModelMotionVert = ShaderText.BeforeEndOfMain(
        ShaderText.After(ModelVert, "layout(location=10) out float vDissolveComplement;", @"
layout(set=1, binding=0) uniform MotionFrame {" + MotionFrameMembersGlsl + @"};
layout(std430, set=1, binding=1) readonly buffer PreviousInstanceTransforms {
    mat4 PrevModel[];     // last frame's world transform of each keyed instance, render-relative
};
layout(location=15) in float IMotionSlot;   // this instance's index into PrevModel, or -1 for its own transform
layout(location=11) out vec4 vCurClip;
layout(location=12) out vec4 vPrevClip;"),
        @"    mat4 prevModel = IMotionSlot < 0.0 ? Model : PrevModel[int(IMotionSlot)];
    vCurClip = CurViewProj * world;
    vPrevClip = MotionParams.x > 0.5 ? PrevViewProj * (prevModel * vec4(Position, 1.0)) : vCurClip;
");
}
