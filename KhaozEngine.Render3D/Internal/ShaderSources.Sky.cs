namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Starfield and sky dome (4 of the renderer's shader sources). The water surface pair moved to
    /// ShaderSources.Water.cs when the stylized-ocean work grew it past the point where it belonged in someone
    /// else's file.
    /// Part of the <see cref="ShaderSources"/> partial: see ShaderSources.cs for the shared contract
    /// (GLSL #version 450, cross-compiled at load via the GPU seam's SPIR-V path).
    /// </summary>
    internal static partial class ShaderSources
    {

        // ---- Procedural starfield. A fullscreen-triangle BACKGROUND pass into the lit colour attachment +
        //      read-only scene depth (ColorDepthFB), identical in shape to the sky pass: the triangle sits at the
        //      FAR plane (z=1) and the pipeline uses a read-only Equal depth test, so a fragment passes ONLY where
        //      the stored depth still EQUALS the cleared far plane, i.e. background pixels where no geometry was
        //      drawn. Geometry (depth < 1) fails Equal and rejects the stars.
        //      This USED to live at the end of BlitFrag, which regenerated the background from the clear colour
        //      after the whole post chain and therefore DISCARDED anything drawn at a background pixel (translucent
        //      content was erased at alpha < 0.5, or punched a star-free hole at alpha >= 0.5). Painting the stars
        //      into the scene before the decals means anything over the void composites over them normally, which
        //      is what release 2's void ground decals need. It also means the stars now flow through the post chain
        //      (quantize/dither/palette, bloom, distortion, HDR tonemap) like everything else, instead of being the
        //      one un-pixelated element pasted on at the very end.
        //      Writes alpha = 1, matching the sky pass, so the background reads as painted for the blit's
        //      TransparentBackground path.
        //      No vertex inputs (gl_VertexIndex only), so the HLSL input signature is empty: no gap-free-holes
        //      hazard (see the D3D11/FXC note on ModelVert).
        public const string StarfieldVert = @"#version 450
void main() {
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(p * 2.0 - 1.0, 1.0, 1.0);   // far plane (z=1): passes the Equal depth test only on background
}";

        // The star field is the EXACT function BlitFrag carried (same 220x124 cell grid, same 0.992 threshold, same
        // 0.55 + 0.45 brightness spread), moved verbatim. The only change is where the UV comes from: gl_FragCoord
        // (upper-left on EVERY backend) times Res.xy = 1/(width,height), the backend-independent convention SkyFrag
        // and DecalFrag use, rather than an interpolated vUv. The star pattern is value noise, so its orientation
        // carries no meaning and is not worth preserving bit-for-bit across the move.
        public const string StarfieldFrag = @"#version 450
layout(set=0, binding=0) uniform Starfield {
    vec4 BgColor;   // rgb = the scene clear colour the stars sit on
    vec4 Res;       // xy = 1/renderWidth, 1/renderHeight
};
layout(location=0) out vec4 oColor;
float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
void main() {
    vec2 uv = gl_FragCoord.xy * Res.xy;
    vec2 cell = floor(uv * vec2(220.0, 124.0));
    float star = step(0.992, hash(cell)) * (0.55 + 0.45 * hash(cell + 3.7));
    oColor = vec4(BgColor.rgb + vec3(star), 1.0);
}";

        // ---- Procedural sky (gradient + sun disc/halo). A fullscreen-triangle BACKGROUND pass rendered into the lit
        //      colour attachment + read-only scene depth (ColorDepthFB), like the ground-decal pass, but INVERTED: the
        //      triangle sits at the FAR plane (z=1) and the pipeline uses an Equal read-only depth test, so a
        //      fragment passes ONLY where the stored depth is still the cleared far plane - i.e. background pixels
        //      where no geometry was drawn. Geometry (depth < 1) rejects the sky, so it never overwrites the scene and
        //      never touches the MRT normal/linear-depth attachments (ColorDepthFB binds only colour + depth). It
        //      writes alpha = 1 as opaque painted background, matching the starfield pass for consistency.
        //      No vertex inputs (gl_VertexIndex only), so the HLSL input signature is empty - no gap-free-holes hazard.
        //      The sky is drawn in SCREEN space (not by a world view ray): under the orthographic iso camera every
        //      view ray is parallel, so a world-ray sky would be a flat colour with no gradient and no localized sun.
        //      A vertical screen gradient + a sun disc placed at the CPU-projected screen position of the sun reads
        //      correctly under both the ortho iso camera and the perspective follow camera. ----
        public const string SkyVert = @"#version 450
void main() {
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(p * 2.0 - 1.0, 1.0, 1.0);   // far plane (z=1): passes the Equal depth test only on background
}";

        // SkyFrag mirrors SkyMath.Shade EXACTLY (keep in sync, like EdgeFrag mirrors OutlineMath). Screen-space: the
        // gradient is a vertical ramp over NDC.y and the sun disc/halo is a screen-space distance to the sun's
        // projected NDC position (SunNdc, computed on the CPU by SkyMath.ProjectSunToNdc so a DIRECTIONAL light with
        // no position still lands on-screen). NDC is rebuilt from gl_FragCoord (upper-left on EVERY backend) + the
        // render size (Res.xy = 1/width,1/height) - the same backend-independent convention DecalFrag uses, NOT an
        // interpolated vertex NDC (which would flip on Vulkan against the geometry). The single UBO holds the colours
        // + projected-sun params + render size; one uniform buffer per set (Metal mis-binds a second). No texture.
        public const string SkyFrag = @"#version 450
layout(set=0, binding=0) uniform Sky {
    vec4 Horizon;     // rgb gradient at the horizon (bottom)
    vec4 Zenith;      // rgb gradient at the zenith (top)
    vec4 SunColor;    // rgb sun disc + halo colour
    vec4 SunNdc;      // xy = sun screen NDC, z = sunVisible (1/0), w = aspect (width/height)
    vec4 Params;      // x=sunEnabled, y=sunRadius, z=haloStrength, w=haloFalloff
    vec4 Res;         // xy = 1/renderWidth, 1/renderHeight
};
layout(location=0) out vec4 oColor;
void main() {
    // NDC from gl_FragCoord (upper-left origin on every backend): x in [-1,1] rightward, y in [-1,1] UPWARD.
    vec2 ndc = vec2(gl_FragCoord.x * Res.x * 2.0 - 1.0, 1.0 - gl_FragCoord.y * Res.y * 2.0);
    // Vertical screen gradient: NDC.y in [-1,1] -> [0,1] (bottom -> top), smoothstep for a soft ramp.
    float up = clamp(ndc.y * 0.5 + 0.5, 0.0, 1.0);
    float t = smoothstep(0.0, 1.0, up);
    vec3 col = mix(Horizon.rgb, Zenith.rgb, t);

    if (Params.x > 0.5 && SunNdc.z > 0.5) {
        float sunRadius = Params.y, haloStrength = Params.z, haloFalloff = Params.w, aspect = SunNdc.w;
        float dx = (ndc.x - SunNdc.x) * aspect;   // aspect-correct so the disc is round in pixels
        float dy = ndc.y - SunNdc.y;
        float d = sqrt(dx * dx + dy * dy);
        float feather = max(haloFalloff * 0.25, 1e-4);
        float disc = 1.0 - smoothstep(sunRadius, sunRadius + feather, d);
        float halo = 0.0;
        if (haloStrength > 0.0 && haloFalloff > 0.0) {
            float beyond = max(0.0, d - sunRadius);
            halo = haloStrength * exp(-beyond / haloFalloff);
        }
        float sun = clamp(disc + halo, 0.0, 1.0);
        col = mix(col, SunColor.rgb, sun);
    }
    oColor = vec4(col, 1.0);   // alpha 1: opaque painted background (consistent with starfield)
}";

    }
}
