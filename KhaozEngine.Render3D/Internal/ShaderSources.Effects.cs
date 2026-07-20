namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Lines, billboards, particles, distortion, beams and trails (13 of the renderer's shader sources).
    /// Part of the <see cref="ShaderSources"/> partial: see ShaderSources.cs for the shared contract
    /// (GLSL #version 450, cross-compiled at load via the GPU seam's SPIR-V path).
    /// </summary>
    internal static partial class ShaderSources
    {

        // ---- Debug line overlay. Standalone mat4 ViewProj UBO (64 bytes), its own layout/buffer,
        //      separate from the model UBO. Depth disabled + alpha blend = overlay. ----
        public const string LineVert = @"#version 450
layout(set=0, binding=0) uniform U { mat4 ViewProj; };
layout(location=0) in vec3 Position;
layout(location=1) in vec4 Color;
layout(location=0) out vec4 vColor;
void main() {
    gl_Position = ViewProj * vec4(Position, 1.0);
    vColor = Color;
}";

        public const string LineFrag = @"#version 450
layout(location=0) in vec4 vColor;
layout(location=0) out vec4 oColor;
void main() {
    oColor = vColor;
}";

        // ---- Camera-facing billboard overlay. Standalone mat4 ViewProj UBO (64 bytes), its own layout/buffer.
        //      Soft disc: alpha falls to 0 toward the corners so the quad reads as a round, feathered sprite.
        //      Depth disabled + alpha-or-additive blend = overlay; drawn after the line pass. ----
        public const string BillboardVert = @"#version 450
layout(set=0, binding=0) uniform U { mat4 ViewProj; };
layout(location=0) in vec3 Position;
layout(location=1) in vec2 Uv;
layout(location=2) in vec4 Color;
layout(location=0) out vec2 vUv;
layout(location=1) out vec4 vColor;
void main() {
    gl_Position = ViewProj * vec4(Position, 1.0);
    vUv = Uv;
    vColor = Color;
}";

        public const string BillboardFrag = @"#version 450
layout(location=0) in vec2 vUv;
layout(location=1) in vec4 vColor;
layout(location=0) out vec4 oColor;
void main() {
    float d = length(vUv * 2.0 - 1.0);
    float a = smoothstep(1.0, 0.55, d);
    oColor = vec4(vColor.rgb, vColor.a * a);
}";

        // ---- Textured, depth-interleaved billboard. Unlike the soft-disc overlay above (which draws AFTER the
        //      post chain with depth disabled), this draws INTO the model MRT alongside the meshes with the depth
        //      test on (no depth write), so a nearer mesh occludes the quad and the quad draws over a farther mesh.
        //      It reuses BillboardVert. It samples a texture sub-rect (vUv carries the source rect) times the tint,
        //      and writes all 3 MRT targets so the SPIR-V output count matches the framebuffer: only colour matters
        //      (the normal/depth attachments use a PreserveDestination blend, so what it writes there is discarded). ----
        public const string TexturedBillboardFrag = @"#version 450
layout(set=0, binding=1) uniform texture2D Tex;
layout(set=0, binding=2) uniform sampler Samp;
layout(location=0) in vec2 vUv;
layout(location=1) in vec4 vColor;
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;
void main() {
    vec4 t = texture(sampler2D(Tex, Samp), vUv);
    oColor = vec4(t.rgb * vColor.rgb, t.a * vColor.a);
    oNormal = vec4(0.0);   // discarded (PreserveDestination blend on attachment 1)
    oDepth  = vec4(0.0);   // discarded (PreserveDestination blend on attachment 2)
}";

        // ---- Modern particle sprites (ParticleRenderer). One instanced draw for the whole frame's queue:
        //      per-instance attributes carry each sprite (locations 0..4, no holes, every attribute consumed,
        //      the D3D11 contiguous-input contract), the two-triangle quad comes from gl_VertexIndex like the
        //      decal pass. Billboarding, spin, and velocity-aligned stretch happen in the vertex stage from the
        //      camera basis in the single Frame UBO (ONE uniform buffer per pipeline, declared identically in
        //      both stages, the Metal contract). The fragment evaluates a procedural SDF/noise shape per sprite
        //      and a soft depth fade against the reconstructed scene surface (the ground-decal texelFetch +
        //      RAW-InvViewProj recipe). Output is PREMULTIPLIED color with alpha scaled by (1 - additivity), so
        //      alpha and additive sprites composite correctly in one back-to-front sorted draw under a single
        //      (One, InverseSourceAlpha) blend state. Draws into ColorDepthFB (lit color + read-only scene
        //      depth) after the water pass and before the post chain, so sprites are occluded by geometry and
        //      additive glow feeds bloom. ----
        public const string ParticleVert = @"#version 450
layout(set=0, binding=0) uniform Frame {
    mat4 ViewProj;      // GpuClip-corrected world->clip (positions the quads per backend)
    mat4 InvViewProj;   // RAW inverse, matching Camera.ScreenToRay (soft-fade reconstruction only)
    vec4 CamRight;      // xyz camera right
    vec4 CamUp;         // xyz camera up
    vec4 CamPosTime;    // xyz eye position, w effect time seconds
    vec4 Params;        // x soft-fade distance (0 = off), y quality (1 full / 0 reduced), z background depth-clear marker, w reserved
};
layout(location=0) in vec4 ICenterSize;   // xyz world center, w half-size
layout(location=1) in vec4 IVelocityRot;  // xyz world velocity, w rotation (radians)
layout(location=2) in vec4 IColor;        // straight rgba tint (premultiplied by the fragment)
layout(location=3) in vec4 IShape;        // x shape id, y shape param, z life norm, w seed
layout(location=4) in vec4 IExtra;        // x stretch, y additivity (0 alpha / 1 additive), z orientation (0 camera / 1 flat ground), w soft-fade scale
layout(location=5) in vec4 IFlip;         // x frameA, y frameB, z blend, w packed grid+strength (0 = procedural)
layout(location=0) out vec2 vLocal;
layout(location=1) out vec4 vColor;
layout(location=2) out vec4 vShape;
layout(location=3) out vec4 vExtra;       // x aspect (stretch elongation), y additivity, z orientation, w soft-fade scale
layout(location=4) out vec3 vWorld;
layout(location=5) out vec4 vFlip;        // flipbook frames + packed grid, passed straight through to the fragment
void main() {
    // Two-triangle quad from gl_VertexIndex (0..5), the same instanced-quad path DecalVert uses.
    float u = (gl_VertexIndex == 1 || gl_VertexIndex == 3 || gl_VertexIndex == 4) ? 1.0 : 0.0;
    float v = (gl_VertexIndex == 2 || gl_VertexIndex == 4 || gl_VertexIndex == 5) ? 1.0 : 0.0;
    vec2 corner = vec2(u, v) * 2.0 - 1.0;

    float size = max(ICenterSize.w, 1e-5);
    // Basis plane: camera-facing (right/up) by default, or flat on the ground (XZ) for shockwave rings and
    // ground glows. The 2D math below (spin, stretch alignment) is identical in either plane.
    vec3 planeX = CamRight.xyz;
    vec3 planeY = CamUp.xyz;
    if (IExtra.z > 0.5) {
        planeX = vec3(1.0, 0.0, 0.0);
        planeY = vec3(0.0, 0.0, 1.0);
    }
    // Velocity-aligned stretch: project the velocity onto the sprite plane. When usable, the quad's local +X
    // follows the in-plane motion direction and lengthens with speed (so the Spark shape's bright head points
    // where the particle is going). Otherwise the quad rolls by its rotation like any round sprite.
    vec2 v2 = vec2(dot(IVelocityRot.xyz, planeX), dot(IVelocityRot.xyz, planeY));
    float speed2 = length(v2);
    float aspect = 1.0;
    vec2 ax;
    if (IExtra.x > 0.0 && speed2 > 1e-4) {
        ax = v2 / speed2;
        aspect = min(1.0 + IExtra.x * speed2 / size, 8.0);
    } else {
        float cr = cos(IVelocityRot.w);
        float sr = sin(IVelocityRot.w);
        ax = vec2(cr, sr);
    }
    vec2 ay = vec2(-ax.y, ax.x);
    vec3 axisX = planeX * ax.x + planeY * ax.y;
    vec3 axisY = planeX * ay.x + planeY * ay.y;
    vec3 world = ICenterSize.xyz + axisX * (corner.x * size * aspect) + axisY * (corner.y * size);
    gl_Position = ViewProj * vec4(world, 1.0);
    vLocal = corner;
    vColor = IColor;
    vShape = IShape;
    vExtra = vec4(aspect, IExtra.y, IExtra.z, IExtra.w);
    vWorld = world;
    vFlip = IFlip;
}";

        public const string ParticleFrag = @"#version 450
layout(set=0, binding=0) uniform Frame {
    mat4 ViewProj;
    mat4 InvViewProj;
    vec4 CamRight;
    vec4 CamUp;
    vec4 CamPosTime;
    vec4 Params;
};
layout(set=0, binding=1) uniform texture2D DepthTex;   // .r = scene NDC depth (single-channel R32F, resolved)
layout(set=0, binding=2) uniform sampler Samp;
// Flipbook atlas + motion-vector sheet. MotionTex sits at binding 3 and AtlasTex at binding 4 on purpose: Metal
// requires every texture be sampled statically in binding order, and the two-tap warp needs the motion vectors
// BEFORE it can offset the atlas taps, so motion must come first. AtlasSamp is the shared linear sampler for both.
layout(set=0, binding=3) uniform texture2D MotionTex;
layout(set=0, binding=4) uniform texture2D AtlasTex;
layout(set=0, binding=5) uniform sampler AtlasSamp;
layout(location=0) in vec2 vLocal;    // quad-local coords in [-1,1] (rotate/stretch with the quad)
layout(location=1) in vec4 vColor;
layout(location=2) in vec4 vShape;    // x shape id, y shape param, z life norm, w seed
layout(location=3) in vec4 vExtra;    // x aspect, y additivity, z orientation (0 camera / 1 flat ground), w soft-fade scale
layout(location=4) in vec3 vWorld;    // fragment world position (flat across the quad's plane)
layout(location=5) in vec4 vFlip;     // x frameA, y frameB, z blend, w packed grid+strength (0 = procedural)
layout(location=0) out vec4 oColor;

// Texture-free value noise, the exact polynomial-hash idiom the decal pass ships cross-backend goldens with
// (sin-based hashes diverge between GPU compilers, this one does not).
float hash21(vec2 p) { p = fract(p * vec2(123.34, 345.45)); p += dot(p, p + 34.345); return fract(p.x * p.y); }
float vnoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash21(i);
    float b = hash21(i + vec2(1.0, 0.0));
    float c = hash21(i + vec2(0.0, 1.0));
    float d = hash21(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

void main() {
    int shape = int(vShape.x + 0.5);
    float param = clamp(vShape.y, 0.0, 1.0);
    float life = clamp(vShape.z, 0.0, 1.0);
    float seed = vShape.w;
    float t = CamPosTime.w;
    float quality = Params.y;
    float d = length(vLocal);

    // Scene depth for the soft fade. Sampled unconditionally up front (Metal's static-sample rule, binding order
    // DepthTex then MotionTex then AtlasTex). The fade MATH below stays gated on fadeDist, so hoisting the fetch
    // here changes no output.
    ivec2 sz = textureSize(sampler2D(DepthTex, Samp), 0);
    float depth = texelFetch(sampler2D(DepthTex, Samp), ivec2(gl_FragCoord.xy), 0).r;

    // Flipbook atlas playback (D-F6 grid decode + D-F3 two-tap motion-vector warp). Sampled statically here, after
    // DepthTex and in binding order (MotionTex then AtlasTex), so Metal stays happy. Procedural sprites pack grid 0
    // (useFlip false) and DISCARD these taps below, keeping their output byte-identical. safeCols guards the mod/div
    // for that discarded path (a real flipbook always has cols >= 1, so it is a no-op there).
    float packedW = vFlip.w;
    bool useFlip = packedW > 0.5;
    float cols = mod(packedW, 256.0);
    float rows = mod(floor(packedW / 256.0), 256.0);
    float mstr = floor(packedW / 65536.0) / 64.0;
    float safeCols = max(cols, 1.0);
    vec2 cell = 1.0 / max(vec2(cols, rows), vec2(1.0));
    vec2 lu = vLocal * 0.5 + 0.5;                          // quad-local [0,1], rotates/stretches with the quad
    vec2 uvA = (vec2(mod(vFlip.x, safeCols), floor(vFlip.x / safeCols)) + lu) * cell;
    vec2 uvB = (vec2(mod(vFlip.y, safeCols), floor(vFlip.y / safeCols)) + lu) * cell;
    vec2 mvA = (texture(sampler2D(MotionTex, AtlasSamp), uvA).rg * 2.0 - 1.0) * mstr * cell;
    vec2 mvB = (texture(sampler2D(MotionTex, AtlasSamp), uvB).rg * 2.0 - 1.0) * mstr * cell;
    float fb = vFlip.z;
    vec4 texA = texture(sampler2D(AtlasTex, AtlasSamp), uvA - mvA * fb);
    vec4 texB = texture(sampler2D(AtlasTex, AtlasSamp), uvB + mvB * (1.0 - fb));
    vec4 flipCol = mix(texA, texB, fb);

    float mask;
    if (shape == 0) {
        // SoftGlow: normalized gaussian-like falloff. Param 0 is the wide classic blob, param 1 a hot center.
        float k = mix(5.0, 14.0, param);
        mask = max(exp(-d * d * k) - exp(-k), 0.0) / (1.0 - exp(-k));
    } else if (shape == 1) {
        // Ember: tight hot core + faint halo, subtle per-particle flicker (Full quality only).
        float coreK = mix(28.0, 10.0, param);
        float flicker = 1.0;
        if (quality > 0.5) flicker = 0.82 + 0.36 * vnoise(vec2(seed * 289.0, t * 7.0));
        mask = (exp(-d * d * coreK) + exp(-d * d * 3.5) * 0.30) * flicker;
    } else if (shape == 2) {
        // Spark: rounded capsule along local +X in aspect-corrected space (caps stay round on screen), with a
        // bright head toward +X (the motion direction under stretch) and a tail that dims by param.
        vec2 q = vec2(vLocal.x * vExtra.x, vLocal.y);
        float capR = 0.30;
        float halfLen = max(vExtra.x - capR, 0.0);
        float sd = length(q - vec2(clamp(q.x, -halfLen, halfLen), 0.0));
        float body = 1.0 - smoothstep(capR * 0.25, capR, sd);
        float along = clamp(q.x / max(vExtra.x, 1e-4) * 0.5 + 0.5, 0.0, 1.0);
        mask = body * (0.35 + 0.65 * pow(along, mix(1.0, 3.0, param)));
    } else if (shape == 3) {
        // Wisp: noise-eroded smoke. The erosion threshold rises with life so the sprite dissolves at its edges
        // instead of fading uniformly. Reduced quality drops the second octave.
        float base = 1.0 - smoothstep(0.25, 1.0, d);
        vec2 np = vLocal * 2.6 + vec2(seed * 157.0, seed * 93.0);
        float n = vnoise(np + vec2(t * 0.35, -t * 0.22));
        if (quality > 0.5) n = 0.65 * n + 0.35 * vnoise(np * 2.3 - vec2(t * 0.5, t * 0.3));
        float erode = mix(0.08, 0.85, life) + (param - 0.5) * 0.3;
        mask = base * smoothstep(erode, erode + 0.28, n * 0.7 + base * 0.5);
    } else if (shape == 4) {
        // Ring: soft annulus for shockwaves. Param widens the band.
        float th = mix(0.055, 0.24, param);
        mask = (1.0 - smoothstep(th * 0.35, th, abs(d - 0.70))) * (1.0 - smoothstep(0.92, 1.0, d));
    } else {
        // Star: four-point glint plus a small hot center. Param sharpens the rays.
        float ang = atan(vLocal.y, vLocal.x);
        float rays = pow(abs(cos(ang * 2.0)), mix(2.0, 10.0, param));
        mask = exp(-d * d * 5.0) * (0.22 + 0.78 * rays) + exp(-d * d * 30.0) * 0.6;
    }

    // Soft depth fade: reconstruct the scene surface behind this fragment (texelFetch at gl_FragCoord + RAW
    // InvViewProj, the backend-independent decal recipe) and fade coverage over Params.x world units of
    // approach. The depth-color attachment is CLEARED to the background color's red channel, not to the far
    // plane, so a sample equal to that marker (Params.z) means no geometry: skip the fade there instead of
    // reconstructing a bogus near-plane point that would dim sprites against empty sky.
    // vExtra.w is the per-sprite soft-fade scale (packed as 1 for the default), and vExtra.z is the orientation
    // (0 camera-facing, 1 flat-on-ground). The fade is SKIPPED for flat-ground sprites: they lie in the ground
    // plane by construction, so the surface reconstructed behind them IS that same coplanar floor, and at a
    // grazing camera angle its reconstructed distance interleaves with the quad's own, erasing the near/far arcs
    // of a shockwave ring (the partially-visible ground-ring bug). Camera-facing sprites still fade over
    // Params.x world units of approach, so a glow sinking into geometry softens at the surface instead of clipping.
    float fade = 1.0;
    float fadeDist = Params.x * vExtra.w;
    if (fadeDist > 0.0 && vExtra.z < 0.5) {
        if (abs(depth - Params.z) > 1e-6) {
            vec4 ndc = vec4(gl_FragCoord.x / float(sz.x) * 2.0 - 1.0, 1.0 - gl_FragCoord.y / float(sz.y) * 2.0, depth, 1.0);
            vec4 wp = InvViewProj * ndc;
            vec3 sceneWorld = wp.xyz / wp.w;
            float sceneDist = distance(sceneWorld, CamPosTime.xyz);
            float fragDist = distance(vWorld, CamPosTime.xyz);
            fade = clamp((sceneDist - fragDist) / fadeDist, 0.0, 1.0);
        }
    }

    // Premultiplied output under a (One, InverseSourceAlpha) blend: alpha sprites keep their coverage in the
    // alpha lane, additive sprites zero it (out = dst + rgb), so one sorted stream composites both correctly.
    // Flipbook sprites take coverage + colour from the atlas frame (tint * sheet). Procedural sprites keep the SDF
    // mask path byte-for-byte.
    float a;
    vec3 rgb;
    if (useFlip) {
        a = clamp(vColor.a * flipCol.a, 0.0, 1.0) * fade;
        rgb = vColor.rgb * flipCol.rgb * a;
    } else {
        a = clamp(vColor.a * mask, 0.0, 1.0) * fade;
        rgb = vColor.rgb * a;
    }
    oColor = vec4(rgb, a * (1.0 - vExtra.y));
}";

        // ---- Screen-space distortion: instanced quads write signed UV offsets into the half/quarter-res
        //      R16G16Float offset field (DistortionRenderer), accumulated additively and re-sampled by the post
        //      apply pass. Reuses the particle pass's proven recipes (gl_VertexIndex quad expansion, camera vs
        //      flat-ground orientation basis, texelFetch depth occlusion with the background-marker skip) but emits
        //      offsets, not colour, and never writes depth (the target has no depth attachment). ----
        public const string DistortionVert = @"#version 450
layout(set=0, binding=0) uniform Frame {
    mat4 ViewProj;      // GpuClip-corrected world->clip
    mat4 InvViewProj;   // RAW inverse, matching Camera.ScreenToRay (depth occlusion reconstruction only)
    vec4 CamRight;      // xyz camera right
    vec4 CamUp;         // xyz camera up
    vec4 CamPosTime;    // xyz eye position, w effect time seconds
    vec4 Params;        // x soft-fade distance (0 = off), y quality (1 full / 0 reduced), z background depth marker, w half-res->full-res texel ratio
};
layout(location=0) in vec4 ICenterSize;   // xyz world center, w half-size
layout(location=1) in vec4 IShapeLife;    // x shape id, y shape param, z life norm, w seed
layout(location=2) in vec4 IExtra;        // x strength, y rotation (radians), z orientation (0 camera / 1 flat ground), w soft-fade scale
layout(location=0) out vec2 vLocal;
layout(location=1) out vec4 vShape;       // x shape id, y shape param, z life norm, w seed
layout(location=2) out vec4 vExtra;       // x strength, y rotation, z orientation, w soft-fade scale
layout(location=3) out vec3 vWorld;
void main() {
    // Two-triangle quad from gl_VertexIndex (0..5), the same instanced-quad path the particle pass uses.
    float u = (gl_VertexIndex == 1 || gl_VertexIndex == 3 || gl_VertexIndex == 4) ? 1.0 : 0.0;
    float v = (gl_VertexIndex == 2 || gl_VertexIndex == 4 || gl_VertexIndex == 5) ? 1.0 : 0.0;
    vec2 corner = vec2(u, v) * 2.0 - 1.0;

    float size = max(ICenterSize.w, 1e-5);
    // Camera-facing (right/up) by default, or flat on the ground plane (XZ) for ground ripples/shockwaves.
    vec3 planeX = CamRight.xyz;
    vec3 planeY = CamUp.xyz;
    if (IExtra.z > 0.5) {
        planeX = vec3(1.0, 0.0, 0.0);
        planeY = vec3(0.0, 0.0, 1.0);
    }
    float cr = cos(IExtra.y);
    float sr = sin(IExtra.y);
    vec2 ax = vec2(cr, sr);
    vec2 ay = vec2(-ax.y, ax.x);
    vec3 axisX = planeX * ax.x + planeY * ax.y;
    vec3 axisY = planeX * ay.x + planeY * ay.y;
    vec3 world = ICenterSize.xyz + axisX * (corner.x * size) + axisY * (corner.y * size);
    gl_Position = ViewProj * vec4(world, 1.0);
    vLocal = corner;
    vShape = IShapeLife;
    vExtra = IExtra;
    vWorld = world;
}";

        public const string DistortionFrag = @"#version 450
layout(set=0, binding=0) uniform Frame {
    mat4 ViewProj;
    mat4 InvViewProj;
    vec4 CamRight;
    vec4 CamUp;
    vec4 CamPosTime;
    vec4 Params;
};
layout(set=0, binding=1) uniform texture2D DepthTex;   // .r = scene NDC depth (single-channel R32F, resolved, full-res)
layout(set=0, binding=2) uniform sampler Samp;
layout(location=0) in vec2 vLocal;    // quad-local coords in [-1,1]
layout(location=1) in vec4 vShape;    // x shape id, y shape param, z life norm, w seed
layout(location=2) in vec4 vExtra;    // x strength, y rotation, z orientation, w soft-fade scale
layout(location=3) in vec3 vWorld;    // fragment world position
layout(location=0) out vec4 oOffset;  // .rg = signed screen-space UV offset (accumulated additively), .ba unused

// Texture-free value noise, the exact polynomial-hash idiom the particle/decal passes ship cross-backend goldens
// with (sin-based hashes diverge between GPU compilers, this one does not).
float hash21(vec2 p) { p = fract(p * vec2(123.34, 345.45)); p += dot(p, p + 34.345); return fract(p.x * p.y); }
float vnoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash21(i);
    float b = hash21(i + vec2(1.0, 0.0));
    float c = hash21(i + vec2(0.0, 1.0));
    float d = hash21(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

void main() {
    int shape = int(vShape.x + 0.5);
    float param = clamp(vShape.y, 0.0, 1.0);
    float seed = vShape.w;
    float strength = vExtra.x;
    float t = CamPosTime.w;
    float quality = Params.y;
    float ratio = Params.w;               // half/quarter-res gl_FragCoord -> full-res texel scale
    float d = length(vLocal);

    // Scene depth for the occlusion fade. The offset target is half/quarter res, so gl_FragCoord is in the small
    // target's pixel space: scale it up by ratio to index the full-res depth texel (and to normalize below). Sampled
    // unconditionally up front (Metal's static-sample rule).
    ivec2 sz = textureSize(sampler2D(DepthTex, Samp), 0);
    float depth = texelFetch(sampler2D(DepthTex, Samp), ivec2(gl_FragCoord.xy * ratio), 0).r;

    vec2 offset;
    if (shape == 0) {
        // Ripple: outward radial offset in a soft ring band around d = 0.7, band width from param.
        float th = mix(0.05, 0.22, param);
        float band = exp(-pow((d - 0.7) / th, 2.0));
        vec2 dir = d > 1e-4 ? vLocal / d : vec2(0.0);
        offset = dir * band;
    } else if (shape == 1) {
        // Heat: upward-scrolling value-noise wobble over the footprint, param scales the frequency. The second
        // octave is Full-quality only (a uniform branch, not a pipeline variant), matching the particle pass.
        float freq = mix(3.0, 9.0, param);
        vec2 p = vLocal * freq + vec2(seed * 41.0, seed * 17.0);
        vec2 up = vec2(0.0, -t * 0.6);    // scroll the noise field over the footprint via effect time
        float nx = vnoise(p + up) - 0.5;
        float ny = vnoise(p + vec2(19.7, 4.3) + up) - 0.5;
        if (quality > 0.5) {
            nx += (vnoise(p * 2.3 + up * 1.7) - 0.5) * 0.5;
            ny += (vnoise(p * 2.3 + vec2(11.1, 7.9) + up * 1.7) - 0.5) * 0.5;
        }
        offset = vec2(nx, ny) * 2.0;
    } else {
        // Lens: smooth radial bulge toward the center. A positive strength magnifies (pull inward), a negative one
        // pinches (push outward), the sign rides on strength below. Param softens the falloff shoulder.
        float falloff = 1.0 - smoothstep(0.0, mix(0.5, 1.0, param), d);
        offset = -vLocal * falloff;
    }

    // Footprint fade so the quad never hard-edges, then the soft depth occlusion (skipping the background marker,
    // the same recipe the particle pass uses), then the authored strength. Fade the offset toward zero (never
    // discard) so edges stay soft. The depth occlusion is SKIPPED for flat-ground sprites (vExtra.z >= 0.5),
    // matching the particle pass: a flat-on-ground refraction ring lies in the coplanar floor, whose reconstructed
    // distance interleaves with the quad's at grazing angles and would erase the ring's near/far arcs.
    float footprint = 1.0 - smoothstep(0.85, 1.0, d);
    float fade = 1.0;
    float fadeDist = Params.x * vExtra.w;
    if (fadeDist > 0.0 && vExtra.z < 0.5) {
        if (abs(depth - Params.z) > 1e-6) {
            vec2 fullUv = gl_FragCoord.xy * ratio / vec2(sz);
            vec4 ndc = vec4(fullUv.x * 2.0 - 1.0, 1.0 - fullUv.y * 2.0, depth, 1.0);
            vec4 wp = InvViewProj * ndc;
            vec3 sceneWorld = wp.xyz / wp.w;
            float sceneDist = distance(sceneWorld, CamPosTime.xyz);
            float fragDist = distance(vWorld, CamPosTime.xyz);
            fade = clamp((sceneDist - fragDist) / fadeDist, 0.0, 1.0);
        }
    }

    oOffset = vec4(offset * strength * footprint * fade, 0.0, 0.0);
}";

        // ---- Additive glowing beam (lasers/thrusters/tethers). Drawn INTO the model MRT alongside the meshes
        //      with the depth test on (no write), so geometry occludes it (like the textured billboard). A
        //      camera-facing strip carries (across,along) UV; the core+halo profile is computed in the fragment
        //      shader from the across coordinate, with optional end taper + time-driven pulse/scroll. Per-beam
        //      style is baked per-vertex (split core/glow colour + two packed param vectors); the only uniform is
        //      ViewProj + Time, so the whole frame's beams render in one draw. Writes all 3 MRT targets to match
        //      the framebuffer; only colour matters (normal/depth use a PreserveDestination blend). ----
        public const string BeamVert = @"#version 450
layout(set=0, binding=0) uniform U { mat4 ViewProj; vec4 Time; };
layout(location=0) in vec3 Position;
layout(location=1) in vec2 Uv;
layout(location=2) in vec4 CoreColor;
layout(location=3) in vec4 GlowColor;
layout(location=4) in vec4 Shape;   // x=coreFrac, y=glowSoftness, z=taper
layout(location=5) in vec4 Anim;    // x=pulseSpeed, y=pulseAmount, z=scrollSpeed
layout(location=0) out vec2 vUv;
layout(location=1) out vec4 vCoreColor;
layout(location=2) out vec4 vGlowColor;
layout(location=3) out vec4 vShape;
layout(location=4) out vec4 vAnim;
void main() {
    gl_Position = ViewProj * vec4(Position, 1.0);
    vUv = Uv;
    vCoreColor = CoreColor;
    vGlowColor = GlowColor;
    vShape = Shape;
    vAnim = Anim;
}";

        public const string BeamFrag = @"#version 450
layout(set=0, binding=0) uniform U { mat4 ViewProj; vec4 Time; };
layout(location=0) in vec2 vUv;
layout(location=1) in vec4 vCoreColor;
layout(location=2) in vec4 vGlowColor;
layout(location=3) in vec4 vShape;   // x=coreFrac, y=glowSoftness, z=taper
layout(location=4) in vec4 vAnim;    // x=pulseSpeed, y=pulseAmount, z=scrollSpeed
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;
void main() {
    float coreFrac = max(vShape.x, 0.02);
    float glowSoft = max(vShape.y, 0.5);
    float taper    = clamp(vShape.z, 0.0, 0.5);
    float d = abs(vUv.x * 2.0 - 1.0);                       // 0 at the axis, 1 at the edge
    float core = 1.0 - smoothstep(coreFrac * 0.6, coreFrac, d);
    float glow = pow(max(1.0 - d, 0.0), glowSoft);
    float taperFade = (taper > 0.0)
        ? smoothstep(0.0, taper, vUv.y) * smoothstep(0.0, taper, 1.0 - vUv.y)
        : 1.0;
    float pulse = 1.0 + vAnim.y * sin(Time.x * vAnim.x);
    float flow  = (vAnim.z != 0.0)
        ? 0.85 + 0.15 * sin((vUv.y - Time.x * vAnim.z) * 6.2831853)
        : 1.0;
    float master = max(taperFade * pulse, 0.0);
    vec3 rgb = vCoreColor.rgb * vCoreColor.a * core * flow
             + vGlowColor.rgb * vGlowColor.a * glow;
    oColor  = vec4(rgb, master);   // Additive (src.a / one): out.rgb = rgb*master + dst.rgb
    oNormal = vec4(0.0);           // discarded (PreserveDestination on attachment 1)
    oDepth  = vec4(0.0);           // discarded (PreserveDestination on attachment 2)
}";

        // ---- Motion-trail ribbon (weapon swings, thruster streaks, tracers): a tapered strip traced along a moving
        //      point, built by TrailGeometry. The tail fade + taper are baked into the vertex (Color.a, geometry);
        //      the fragment only feathers the across-width edge (like the beam does its core/glow). One vertex shader
        //      feeds both blend pipelines: the output vec4(rgb, a) is what Additive (src.a/1) and AlphaBlend
        //      (src.a/1-src.a) both consume. Writes all 3 MRT targets; attachments 1 & 2 preserve destination. ----
        public const string TrailVert = @"#version 450
layout(set=0, binding=0) uniform U { mat4 ViewProj; };
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Uv;      // x=across, y=along, z=softEdge
layout(location=2) in vec4 Color;   // rgb tint, a = style.alpha * sample.alpha
layout(location=0) out vec3 vUv;
layout(location=1) out vec4 vColor;
void main() {
    gl_Position = ViewProj * vec4(Position, 1.0);
    vUv = Uv;
    vColor = Color;
}";

        public const string TrailFrag = @"#version 450
layout(location=0) in vec3 vUv;     // x=across, y=along, z=softEdge
layout(location=1) in vec4 vColor;
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;
void main() {
    float d = abs(vUv.x * 2.0 - 1.0);                  // 0 at the axis, 1 at the edge
    float soft = clamp(vUv.z, 0.0, 1.0);
    float edge = 1.0 - smoothstep(1.0 - soft, 1.0, d); // feather the across-width edge
    float a = vColor.a * edge;
    oColor  = vec4(vColor.rgb, a);   // Additive: rgb*a + dst; AlphaBlend: rgb*a + dst*(1-a)
    oNormal = vec4(0.0);             // discarded (PreserveDestination on attachment 1)
    oDepth  = vec4(0.0);             // discarded (PreserveDestination on attachment 2)
}";
    }
}
