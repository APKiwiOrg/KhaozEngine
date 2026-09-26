namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// The temporal variants of the two ground passes (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 section 3). Ground never moves,
/// so its previous clip position is its world position under last frame's view-projection: camera-only motion. The
/// motion block sits at set 2, after the ground's frame and material sets. Part of the <see cref="ShaderSources"/> partial.
/// </summary>
internal static partial class ShaderSources
{
    /// <summary>SplatVert with motion. The pair joins SplatFrag's gap-free read block at 5 and 6, and the three
    /// fragment-unused outputs SplatVert parks at 5 to 7 move to 7 to 9, still above everything the fragment reads
    /// (docs/CROSS-PLATFORM.md). Every declared input is still read, so the vertex input signature stays whole.</summary>
    public const string SplatMotionVert = @"#version 450
" + CompactFrameBlockGlsl + @"layout(set=2, binding=0) uniform MotionFrame {" + MotionFrameMembersGlsl + @"};
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=4) in vec4 Tangent;
layout(location=5) in vec4 IModel0;
layout(location=6) in vec4 IModel1;
layout(location=7) in vec4 IModel2;
layout(location=8) in vec4 IModel3;
layout(location=9) in vec4 ITint;
layout(location=10) in vec4 IEmissive;
layout(location=11) in vec4 ISpecParams;
layout(location=0) out vec3 vNormalW;
layout(location=1) out vec4 vColor;
layout(location=2) out vec3 vWorldPos;
layout(location=3) out vec4 vTint;
layout(location=4) out vec4 vEmissive;
layout(location=5) out vec4 vCurClip;
layout(location=6) out vec4 vPrevClip;
layout(location=7) out vec2 vUv;          // fragment-unused, above the live block as in SplatVert
layout(location=8) out vec4 vSpecParams;  // fragment-unused
layout(location=9) out vec4 vTangent;     // fragment-unused
void main() {
    mat4 Model = mat4(IModel0, IModel1, IModel2, IModel3);
    vec4 world = Model * vec4(Position, 1.0);
    gl_Position = ViewProj * world;
    vNormalW = normalize(mat3(Model) * Normal);
    vColor = Color;
    vWorldPos = world.xyz;
    vUv = TexCoord;
    vTint = ITint;
    vEmissive = IEmissive;
    vSpecParams = ISpecParams;
    vTangent = vec4(mat3(Model) * Tangent.xyz, Tangent.w);
    vCurClip = CurViewProj * world;
    vPrevClip = MotionParams.x > 0.5 ? PrevViewProj * world : vCurClip;
}";

    /// <summary>SplatFrag with motion, the pair at 5 and 6.</summary>
    public static readonly string SplatMotionFrag = MotionFragment(SplatFrag, 5);

    /// <summary>TileGroundVert with motion, the pair after its seven outputs at 7 and 8.</summary>
    public static readonly string TileGroundMotionVert = ShaderText.BeforeEndOfMain(
        ShaderText.After(TileGroundVert, "layout(location=6) out vec4 vEmissive;", @"
layout(set=2, binding=0) uniform MotionFrame {" + MotionFrameMembersGlsl + @"};
layout(location=7) out vec4 vCurClip;
layout(location=8) out vec4 vPrevClip;"),
        @"    vCurClip = CurViewProj * world;
    vPrevClip = MotionParams.x > 0.5 ? PrevViewProj * world : vCurClip;
");

    /// <summary>TileGroundFrag with motion, the pair at 7 and 8.</summary>
    public static readonly string TileGroundMotionFrag = MotionFragment(TileGroundFrag, 7);
}
