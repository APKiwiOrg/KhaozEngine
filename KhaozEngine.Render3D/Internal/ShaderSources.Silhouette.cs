namespace KhaozEngine.Render3D.Internal
{
    internal static partial class ShaderSources
    {
        // ---- Entity silhouette (inverted hull): re-draw a model mesh with every vertex pushed along its world
        //      normal by a uniform width, FRONT faces culled, flat colour, depth tested without writing. Only the
        //      rim outside the model's own silhouette survives, occluded correctly by nearer scene geometry. This
        //      is the per-entity highlight (a clicked monster, a selected prop); the whole-scene edge detect on
        //      PixelPostProcessSettings.Outline is a different feature and keeps the Outline name.
        //
        //      Declares the full ModelVertex layout so the model pass's GPU vertex buffer binds unchanged, with
        //      the same load-bearing D3D11/FXC sink OverlayUnlitVert carries: Position and Normal are read for
        //      real, and Color, TexCoord and Tangent ride the negligible-but-live sink so SPIRV-Cross keeps a
        //      CONTIGUOUS TEXCOORD0..4 vertex input signature. Do NOT remove the sink or any of its reads.
        public const string SilhouetteVert = @"#version 450
layout(set=0, binding=0) uniform Draw { mat4 ViewProj; mat4 World; vec4 Color; vec4 Params; };
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 VColor;
layout(location=3) in vec2 TexCoord;
layout(location=4) in vec4 Tangent;
void main() {
    // Push along the WORLD-space normal so the shell's width is in metres whatever the mesh's local scale.
    // The props use uniform scale, so the rotation part of World carries the normal faithfully after a
    // normalize. Params.x is the width in metres.
    vec3 worldNormal = normalize(mat3(World) * Normal);
    vec4 world = World * vec4(Position, 1.0);
    world.xyz += worldNormal * Params.x;
    // The sink, as in OverlayUnlitVert: keeps the HLSL vertex input signature gap-free for FXC/WARP.
    float sink = VColor.x + TexCoord.x + Tangent.x;
    world.x += sink * 1e-30;
    gl_Position = ViewProj * world;
}";

        public const string SilhouetteFrag = @"#version 450
layout(set=0, binding=0) uniform Draw { mat4 ViewProj; mat4 World; vec4 Color; vec4 Params; };
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;
void main() {
    oColor = Color;        // flat, alpha via the AlphaBlend attachment on target 0
    oNormal = vec4(0.0);   // discarded (PreserveDestination blend on attachment 1)
    oDepth  = vec4(0.0);   // discarded (PreserveDestination blend on attachment 2)
}";
    }
}
