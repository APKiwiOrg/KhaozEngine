namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Starfield, sky dome and water (6 of the renderer's shader sources).
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

        // ---- Animated water surface (Rendering gap #5). Drawn AFTER the sky and the ground decals into
        //      ColorDepthFB (lit colour + read-only scene depth), a CPU-tessellated flat grid (WaterMath.GridResolution)
        //      at the plane's world height. Depth test ON (Less, standard, so terrain/props above the surface occlude
        //      it, matching the textured-billboard/beam depth-interleave convention) but depth WRITE OFF: the outline
        //      pass reads the resolved normal/linear-depth MRT (ColorTex's siblings), and those are captured by the
        //      OPAQUE model pass alone (see RenderResources.ResolveDepthNormal/ResolveColor, which run BEFORE this
        //      pass in Scene3D.RenderInternal) - a water depth WRITE would need its own MRT write to keep that
        //      buffer meaningful, which reflections/probes (out of scope, roadmap #9) would want but this LDR pass
        //      does not attempt. No-write keeps the edge outline tracing the solid geometry's silhouette (a
        //      shore-line water edge is desirable per the brief; a corrupted normal/depth buffer that broke the
        //      outline pass for EVERYTHING behind the water is not). The surface itself is three domain-warped,
        //      non-axis-aligned scrolling wave layers at mutually irrational frequencies (so the field has no
        //      finite tile), with the fine layers attenuated by camera distance and the body colour blended toward
        //      a shallow tint as the reconstructed ground rises to the surface. Two textures bound: the resolved scene depth
        //      (shore fade, decal-style gl_FragCoord reconstruction) and nothing else - no second material texture,
        //      so the Metal up-front-sample-order landmine does not apply here (only one texture total). Vertex
        //      inputs are Position only (no gap-free-signature hazard: everything declared is read). One UBO
        //      (fragment-only; the vertex only needs ViewProj, folded into the SAME buffer per the one-UBO-per-set
        //      rule, read by both stages). ----
        public const string WaterVert = @"#version 450
layout(set=0, binding=2) uniform Water {
    mat4 ViewProj;
    mat4 InvViewProj;   // RAW (not clip-corrected) inverse, for the fragment's depth reconstruction
    vec4 LightDir;      // xyz = key light travel direction
    vec4 LightColor;
    vec4 CameraPos;     // xyz = eye position
    vec4 DeepColor;     // rgb + alpha
    vec4 ShallowColor;  // rgb + alpha, the body colour where the ground meets the surface
    vec4 HorizonColor;  // rgb + alpha
    vec4 WaveParams;    // x=waveScale, y=waveSpeed, z=normalStrength, w=time
    vec4 ShoreGlint;    // x=shoreFadeDistance, y=glintStrength, z=glintExponent, w=opacity
    vec4 DetailParams;  // x=warpStrength, y=detailFadeDistance, z=distantDetailScale, w=shallowDepth
};
layout(location=0) in vec3 Position;
layout(location=0) out vec3 vWorldPos;
void main() {
    gl_Position = ViewProj * vec4(Position, 1.0);
    vWorldPos = Position;
}";

        public const string WaterFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D DepthTex;   // .r = resolved scene linear depth (single-channel R32F)
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform Water {
    mat4 ViewProj;
    mat4 InvViewProj;
    vec4 LightDir;
    vec4 LightColor;
    vec4 CameraPos;
    vec4 DeepColor;
    vec4 ShallowColor;
    vec4 HorizonColor;
    vec4 WaveParams;    // x=waveScale, y=waveSpeed, z=normalStrength, w=time
    vec4 ShoreGlint;    // x=shoreFadeDistance, y=glintStrength, z=glintExponent, w=opacity
    vec4 DetailParams;  // x=warpStrength, y=detailFadeDistance, z=distantDetailScale, w=shallowDepth
};
layout(location=0) in vec3 vWorldPos;
layout(location=0) out vec4 oColor;

// Mirrors WaterMath.DomainWarp exactly: a slow, large-scale displacement of the sample position applied BEFORE the
// wave layers, so their pattern is bent over a distance several times their own wavelength. Its Jacobian is
// deliberately NOT folded into the analytic slope below (see the WaterMath.DomainWarp note).
vec2 domainWarp(vec2 xz, float scrollTime, float waveScale, float warpStrength) {
    if (warpStrength <= 0.0) return xz;
    float scale = max(waveScale, 1e-4);
    float k = 0.21 / scale;
    float wt = scrollTime * 0.23;
    float ax = sin(xz.y * k + wt) + 0.7 * sin(xz.x * k * 0.57 - wt * 1.31);
    float az = cos(xz.x * k - wt * 0.79) + 0.7 * cos(xz.y * k * 0.57 + wt * 1.17);
    return xz + vec2(ax, az) * (warpStrength * scale);
}

// Mirrors WaterMath.DetailScale exactly: 1 at the camera, falling to distantScale at/after fadeDistance, so the
// fine wave layers stop aliasing into a crawling moire (and the tight glint into sparkle) in the far field.
float detailScaleFor(float camDist, float fadeDistance, float distantScale) {
    if (fadeDistance <= 0.0) return 1.0;
    float s = smoothstep(0.0, fadeDistance, camDist);
    return mix(1.0, clamp(distantScale, 0.0, 1.0), s);
}

// Mirrors WaterMath.WaveNormal exactly: three non-axis-aligned scrolling wave layers at mutually irrational
// frequencies over the warped position, analytic slope -> tilted flat-up normal. Layers 2 and 3 (the fine detail)
// are scaled by `detail`; layer 1 (the broad swell) always runs full. See WaterMath for why the field is shaped
// this way rather than as two axis-aligned octaves.
vec3 waterNormal(vec2 xz, float time, float waveScale, float waveSpeed, float normalStrength,
                 float warpStrength, float detail) {
    float scale = max(waveScale, 1e-4);
    float invScale = 1.0 / scale;
    float t = time * waveSpeed;
    vec2 p = domainWarp(xz, t, waveScale, warpStrength);

    vec2 d1 = vec2(0.93969262, 0.34202014);    // 20 deg
    vec2 d2 = vec2(-0.42261826, 0.90630779);   // 115 deg
    vec2 d3 = vec2(0.62932039, -0.77714596);   // -51 deg
    float k1 = invScale * 1.0, k2 = invScale * 1.61803399, k3 = invScale * 2.64575131;
    float g1 = 1.0 * k1 * cos(dot(d1, p) * k1 + t * 1.0);
    float g2 = 0.62 * k2 * cos(dot(d2, p) * k2 + t * -1.37) * detail;
    float g3 = 0.32 * k3 * cos(dot(d3, p) * k3 + t * 0.83) * detail;

    float dHdx = g1 * d1.x + g2 * d2.x + g3 * d3.x;
    float dHdz = g1 * d1.y + g2 * d2.y + g3 * d3.y;
    vec3 n = vec3(-dHdx * normalStrength, 1.0, -dHdz * normalStrength);
    float len = length(n);
    return len > 1e-8 ? n / len : vec3(0.0, 1.0, 0.0);
}

void main() {
    float waveScale = WaveParams.x, waveSpeed = WaveParams.y, normalStrength = WaveParams.z, time = WaveParams.w;
    float warpStrength = DetailParams.x, detailFadeDist = DetailParams.y;
    float distantDetail = DetailParams.z, shallowDepth = DetailParams.w;

    // One eye vector, used for the view direction, the fresnel term AND the detail fade's camera distance.
    vec3 toEye = CameraPos.xyz - vWorldPos;
    float camDist = length(toEye);
    vec3 V = camDist > 1e-8 ? toEye / camDist : vec3(0.0, 1.0, 0.0);
    float detail = detailScaleFor(camDist, detailFadeDist, distantDetail);
    vec3 N = waterNormal(vWorldPos.xz, time, waveScale, waveSpeed, normalStrength, warpStrength, detail);

    // Ground reconstruction: recover the world position of whatever the opaque pass left under this pixel, from the
    // resolved scene depth (the ground-decal pass's gl_FragCoord + raw-inverse-view-projection convention -
    // backend-independent, unlike an interpolated UV, because render-target texture SAMPLING has a
    // backend-dependent Y origin while gl_FragCoord is upper-left on every backend). depthBelowSurface = this water
    // fragment's own world Y minus the ground's world Y (positive when the ground sits below the surface, as it
    // must for this fragment to have passed the water pass's OWN depth test in the first place). BOTH the shallow
    // body-colour blend and the waterline alpha feather key off it, so it is computed once, up front.
    ivec2 sz = textureSize(sampler2D(DepthTex, Samp), 0);
    float groundDepth = texelFetch(sampler2D(DepthTex, Samp), ivec2(gl_FragCoord.xy), 0).r;
    vec4 ndc = vec4(gl_FragCoord.x / float(sz.x) * 2.0 - 1.0, 1.0 - gl_FragCoord.y / float(sz.y) * 2.0, groundDepth, 1.0);
    vec4 wp = InvViewProj * ndc;
    vec3 groundWorld = wp.xyz / wp.w;
    float depthBelowSurface = vWorldPos.y - groundWorld.y;

    // Shallow blend (mirrors WaterMath.ShallowWeight/ShallowTint): the BODY colour shifts toward ShallowColor as
    // the ground rises to meet the surface. Applied BEFORE the fresnel blend so a grazing view of the shallows
    // still picks up the horizon/sky tint on top instead of losing it to the shore colour.
    float shallowW = shallowDepth <= 0.0 ? 0.0
        : 1.0 - smoothstep(0.0, 1.0, clamp(depthBelowSurface / shallowDepth, 0.0, 1.0));
    vec3 body = mix(DeepColor.rgb, ShallowColor.rgb, shallowW);
    float bodyAlpha = mix(DeepColor.a, ShallowColor.a, shallowW);

    float ndotv = clamp(dot(N, V), 0.0, 1.0);
    // Schlick-style fresnel: (1-ndotv)^5, mirrors WaterMath.Fresnel.
    float fx = clamp(1.0 - ndotv, 0.0, 1.0);
    float fresnel = fx * fx * fx * fx * fx;
    vec3 tint = mix(body, HorizonColor.rgb, fresnel);
    float tintAlpha = mix(bodyAlpha, HorizonColor.a, fresnel);

    // Key-light specular sun glint: small water-specific Blinn-Phong term (mirrors WaterMath.SunGlint), NOT routed
    // through the shared computeLighting block (water needs its own tight strength/exponent, distinct from any
    // mesh material).
    float glintStrength = ShoreGlint.y, glintExponent = ShoreGlint.z;
    vec3 Lsun = -normalize(LightDir.xyz);
    vec3 H = V + Lsun;
    float hLen = length(H);
    float glint = 0.0;
    if (glintStrength > 0.0 && hLen > 1e-8) {
        H /= hLen;
        float ndoth = max(dot(N, H), 0.0);
        glint = pow(ndoth, max(glintExponent, 1.0)) * glintStrength;
    }

    // Waterline alpha feather (mirrors WaterMath.ShoreFade): a much tighter distance than the shallow tint above,
    // so the very edge softens out instead of clipping against the seabed.
    float shoreFadeDist = ShoreGlint.x;
    float shoreFade = shoreFadeDist <= 0.0 ? 1.0 : smoothstep(0.0, 1.0, clamp(depthBelowSurface / shoreFadeDist, 0.0, 1.0));

    float opacity = ShoreGlint.w;
    vec3 rgb = tint + LightColor.rgb * glint;
    float alpha = tintAlpha * opacity * shoreFade;
    if (alpha <= 0.001) discard;
    oColor = vec4(rgb, alpha);
}";
    }
}
