namespace KhaozEngine.Render3D.Internal
{
    /// <summary>The temporal resolve's debug view and count probe programs. Part of the <see cref="ShaderSources"/>
    /// partial. Each splices the resolve's own per-pixel function, so it shows exactly the decisions the resolve made
    /// rather than a second derivation that could drift from it.</summary>
    internal static partial class ShaderSources
    {
        // ---- The History, Disocclusion and Reactive debug views: one fullscreen pass over the final target that
        //      replaces the image. Set 0 is the resolve's own layout and the set this frame's resolve bound, set 1 one
        //      immutable mode block per view. The display pixel is taken upright from vUv, the orientation
        //      TargetOutlineCompositeFrag reads a same-size target in. ----
        public const string TemporalDebugFrag = @"#version 450
" + TemporalResolveCoreGlsl + @"
// Mode.x is the SceneDebugView value: 2 History, 3 Disocclusion, 4 Reactive.
layout(set=1, binding=0) uniform DebugView { vec4 Mode; };
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() {
    ivec2 size = temporalDisplaySize();
    ivec2 p = clamp(ivec2(vec2(vUv.x, 1.0 - vUv.y) * vec2(size)), ivec2(0), size - ivec2(1));
    TemporalPixel t = temporalResolvePixel(p);
    float l = dot(max(t.color, vec3(0.0)), vec3(0.2126, 0.7152, 0.0722));
    vec3 context = vec3(0.35 * l / (1.0 + l));   // the scene, dimmed, so each overlay reads against it
    int mode = int(Mode.x + 0.5);
    vec3 c;
    if (mode == 2) {
        c = vec3(clamp(t.confidence, 0.0, 1.0));   // the weight stored for the next frame: black fresh, white full
        if (t.stability >= 0.5) c = mix(c, vec3(0.1, 0.95, 0.25), 0.6);   // held by thin feature retention
    } else if (mode == 3) {
        c = t.disocclusion >= 0.5 ? vec3(1.0, 0.1, 0.1) : context;
    } else {
        c = mix(context, vec3(1.0, 0.85, 0.1), clamp(t.reactive, 0.0, 1.0));
    }
    oColor = vec4(c, 1.0);
}";

        // ---- The on-request count probe: one texel per cell of a 32 by 18 grid over the display, each the share of
        //      16 evenly spread samples the resolve treated as disoccluded (r), reactive (g) or clipped (b). The
        //      resolve's own function is evaluated at each sample over this frame's resolve set, so the counts are
        //      the resolve's decisions. Read back on request only (TemporalCountProbe). ----
        public const string TemporalProbeFrag = @"#version 450
" + TemporalResolveCoreGlsl + @"
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oCounts;
void main() {
    ivec2 size = temporalDisplaySize();
    vec2 cell = floor(vec2(vUv.x, 1.0 - vUv.y) * vec2(32.0, 18.0));
    vec3 hits = vec3(0.0);
    for (int sy = 0; sy < 4; sy++) {
        for (int sx = 0; sx < 4; sx++) {
            vec2 at = (cell + (vec2(float(sx), float(sy)) + 0.5) / 4.0) / vec2(32.0, 18.0);
            ivec2 p = clamp(ivec2(at * vec2(size)), ivec2(0), size - ivec2(1));
            TemporalPixel t = temporalResolvePixel(p);
            hits += vec3(t.disocclusion >= 0.5 ? 1.0 : 0.0, t.reactive >= 0.5 ? 1.0 : 0.0, t.clip >= 0.5 ? 1.0 : 0.0);
        }
    }
    oCounts = vec4(hits / 16.0, 1.0);
}";
    }
}
