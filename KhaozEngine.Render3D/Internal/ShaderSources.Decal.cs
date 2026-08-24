namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Projected decals (2 of the renderer's shader sources).
    /// Part of the <see cref="ShaderSources"/> partial: see ShaderSources.cs for the shared contract
    /// (GLSL #version 450, cross-compiled at load via the GPU seam's SPIR-V path).
    /// </summary>
    internal static partial class ShaderSources
    {

        // ---- Ground decal: paint an analytic danger-zone shape onto the surface under each pixel. Reconstructs the
        //      surface world position from the sampled linear depth (DepthTex) via InvViewProj, evaluates the shape
        //      SDF in shape-local space on the XZ plane, gates by a Y-band around the ground height (so it conforms
        //      to terrain but does not climb walls), and blends fill+outline with an fwidth AA edge. Renders into
        //      ColorTex (ColorOnlyFB) before the post chain, with alpha or additive blend. ----
        // BATCHED + FOOTPRINT-BOUNDED: one INSTANCED draw paints N decals of one blend (GroundDecalRenderer coalesces
        // consecutive same-blend decals into runs, preserving submission order so overlapping decals still composite
        // correctly). Each per-decal parameter moves from a per-draw UBO slot to a per-instance vertex ATTRIBUTE,
        // consumed directly (positioning the quad, then passed to the fragment stage) - never used to index a buffer,
        // the Metal-safe instancing invariant (ModelVert's IModel*/ITint are the production proof). Instead of a
        // fullscreen triangle per decal, each instance emits a screen-space QUAD covering the decal's projected ground
        // footprint (IScreenRect, computed CPU-side), at the FAR plane (z=1). The pass renders with the scene
        // depth-stencil bound read-only and a Greater depth test, so a fragment passes only where the stored depth is
        // nearer than the far plane - i.e. only on scene geometry (background at the cleared far plane fails). The
        // bounded quad only shrinks the RASTERIZED area (the fullscreen version discarded every pixel outside the
        // footprint anyway), so the shaded output is identical while fill scales with decal area, not viewport area.
        // ONE VERTEX SHADER, TWO PIPELINES: a decal with GroundDecal.VoidFallback set is drawn a SECOND time by an
        // otherwise identical pipeline whose depth test is Equal at z=1 - the exact complement of Greater at z=1, so
        // it passes on BACKGROUND only. That instance carries IExtra.y = 1 and the fragment shader projects onto the
        // decal's own plane instead of reconstructing from depth. Unflagged decals emit no such instance, so the
        // Equal pipelines are never bound and the pass is bit-identical to the pre-feature one.
        public const string DecalVert = @"#version 450
layout(location=0) in vec4 IScreenRect;  // per-instance ndc footprint rect (minX, minY, maxX, maxY)
layout(location=1) in vec4 ICenter;      // xyz world center, w = rotation
layout(location=2) in vec4 ISize;
layout(location=3) in vec4 IFill;
layout(location=4) in vec4 IOutline;
layout(location=5) in vec4 IParams;
layout(location=6) in vec4 IGate;
layout(location=7) in vec4 IPattern;   // x=pattern index, y=speed, z=cells per world unit, w=interiorDim
layout(location=8) in vec4 IEnergy;    // x=rimGlow, y=sweepGlow, z=sparkle, w=runner
layout(location=9) in vec4 IExtra;     // x=baseFill, y=voidPath (0/1), z=voidDim, w=wantsFallback (0/1)
layout(location=10) in vec4 IAccent;   // MoltenCracks hot colour (rgb + a), zero for every other pattern
layout(location=11) in vec4 IMisc;     // x=patternParam, y=edgeErosion, z/w reserved
layout(location=0) out vec4 vCenter;
layout(location=1) out vec4 vSize;
layout(location=2) out vec4 vFill;
layout(location=3) out vec4 vOutline;
layout(location=4) out vec4 vParams;
layout(location=5) out vec4 vGate;
layout(location=6) out vec4 vPattern;
layout(location=7) out vec4 vEnergy;
layout(location=8) out vec4 vExtra;
layout(location=9) out vec4 vAccent;
layout(location=10) out vec4 vMisc;
void main() {
    // Two-triangle quad (gl_VertexIndex 0..5) spanning the instance's NDC footprint rect. Each per-instance attribute
    // is identical across the quad's six vertices, so the smooth varyings deliver the exact per-instance value to the
    // fragment stage (the same constant-across-the-primitive path ModelVert's per-instance outputs rely on).
    float u = (gl_VertexIndex == 1 || gl_VertexIndex == 3 || gl_VertexIndex == 4) ? 1.0 : 0.0;
    float v = (gl_VertexIndex == 2 || gl_VertexIndex == 4 || gl_VertexIndex == 5) ? 1.0 : 0.0;
    vec2 ndc = mix(IScreenRect.xy, IScreenRect.zw, vec2(u, v));
    gl_Position = vec4(ndc, 1.0, 1.0);   // far plane (z=1): the Greater read-only depth test passes over geometry only
    vCenter = ICenter;
    vSize = ISize;
    vFill = IFill;
    vOutline = IOutline;
    vParams = IParams;
    vGate = IGate;
    vPattern = IPattern;
    vEnergy = IEnergy;
    vExtra = IExtra;
    vAccent = IAccent;
    vMisc = IMisc;
}";

        public const string DecalFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D DepthTex;   // .r = linear depth (single-channel R32F)
layout(set=0, binding=1) uniform sampler Samp;
// ONE uniform buffer, which the retired Veldrid Metal backend needed (it mis-bound a second) and #604 no longer
// requires: the RAW inverse view-projection and the time/quality value share this single Frame block, grown from
// 64 to 80 bytes, and nothing here wants a second one.
layout(set=0, binding=2) uniform Frame {
    mat4 InvViewProj;   // RAW (un-clip-corrected) inverse view-projection, shared by every decal this frame
    vec4 TimeQ;         // x = effect time seconds, y = quality (1 full / 0 reduced), z = maxRgb ceiling,
                        // w = dynamic-geometry reject (1 on, read on the GEOMETRY path below, issue #235)
};
// GEOMETRIC world normal, encoded *0.5+0.5 by the model pass, ALPHA 0 on dynamic/skinned surfaces. Read on two
// paths below: the void fallback (is this the decal's ground or a cliff face) and the TimeQ.w dynamic reject.
layout(set=0, binding=3) uniform texture2D NormalTex;

// Minimum world-up component for a surface to count as a decal's GROUND rather than a wall. n.y is cos(angle from
// vertical): 1 = flat, 0 = a vertical face. 0.5 admits slopes up to 60 degrees, so real terrain still receives the
// decal while a cliff or wall face never does.
const float GroundNormalMinY = 0.5;
layout(location=0) in vec4 Center;    // xyz world center, w = rotation (radians about +Y)
layout(location=1) in vec4 Size;      // per-shape params (see GroundDecal.Size)
layout(location=2) in vec4 Fill;      // rgb, a = fill alpha (already opacity-scaled)
layout(location=3) in vec4 Outline;   // rgb, a = outline alpha
layout(location=4) in vec4 Params;    // x=edgeThickness, y=fillFraction, z=flashAdd, w=shapeIndex
layout(location=5) in vec4 Gate;      // x=groundY, y=yTolerance, z=maxStep, w=featherWidth (world units)
layout(location=6) in vec4 PatternP;  // x=pattern index, y=speed (cycles/s), z=cells per world unit, w=interiorDim
layout(location=7) in vec4 Energy;    // x=rimGlow, y=sweepGlow, z=sparkle, w=runner
layout(location=8) in vec4 Extra;     // x=baseFill, y=voidPath (0 = depth-reconstruct, 1 = plane-project), z=voidDim,
                                      // w = wantsFallback: this decal asked for the plane fallback (geometry pass only)
layout(location=9) in vec4 Accent;    // MoltenCracks hot colour (rgb = crack glow tint, a = crack alpha); zero otherwise
layout(location=10) in vec4 Misc;     // x=patternParam (MoltenCracks: crack width in cell units), y=edgeErosion, z/w reserved
layout(location=0) out vec4 oColor;

// 2D SDFs in shape-local space (origin at decal center, +x along the decal's facing for oriented shapes).
float sdCircle(vec2 p, float r) { return length(p) - r; }
float sdRing(vec2 p, float ri, float ro) { float d = length(p); return max(ri - d, d - ro); }
float sdBox(vec2 p, vec2 b) { vec2 d = abs(p) - b; return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0); }

// Intersect this pixel's camera ray with the horizontal plane y = planeY. The ray is built exactly the way
// Camera.ScreenToRay builds it (unproject at NDC depth 0 and 1, direction = far - near, NOT normalized), which is
// correct for both the orthographic iso camera (constant direction) and a perspective one (the w-divide fans it out).
// Returns false when the ray is parallel to the plane or the plane lies behind the eye. tOut is the hit's parameter
// along near->far, so it is directly comparable against ANY other point's parameter on the SAME ray - which is how
// the geometry path below depth-tests the plane against a real surface.
bool planeHit(vec2 ndcXY, float planeY, out vec3 hit, out float tOut, out vec3 ro, out vec3 rd)
{
    vec4 n4 = InvViewProj * vec4(ndcXY, 0.0, 1.0);
    vec4 f4 = InvViewProj * vec4(ndcXY, 1.0, 1.0);
    ro = n4.xyz / n4.w;
    rd = f4.xyz / f4.w - ro;
    if (abs(rd.y) < 1e-6) return false;
    tOut = (planeY - ro.y) / rd.y;
    if (tOut < 0.0) return false;
    hit = ro + rd * tOut;
    return true;
}

// Texture-free value noise for the animated fill patterns + sparkle. hash21 hashes a cell corner to a [0,1) scalar.
// vnoise smoothly interpolates the four corners of the unit cell containing p (Perlin-style smootherstep weights).
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

// MoltenCracks feature point for a Voronoi cell, in cell space. Deterministic per cell (hash of the integer cell
// id only) with a slow sinusoidal drift around the cell centre - the ""molten breathing"". Every client evaluating
// the same cell at the same effect time gets the same point, so the crack web is multiplayer-consistent.
vec2 crackPoint(vec2 cell, float t)
{
    vec2 h = vec2(hash21(cell), hash21(cell + 91.7));
    return cell + 0.5 + 0.42 * sin(6.2831853 * h + t * vec2(3.1, 2.3));
}

// Distance from qp (cell space) to the nearest Voronoi CELL BORDER, the two-pass exact form (Quilez's
// voronoiDistance): pass 1 finds the nearest feature point, pass 2 measures the distance to the perpendicular
// bisector between it and each neighbour. outCell returns the owning cell id for the per-cell heat pulse.
float crackBorderDist(vec2 qp, float t, out vec2 outCell)
{
    vec2 n = floor(qp);
    vec2 mg = vec2(0.0), mr = vec2(0.0);
    float md = 8.0;
    for (int j = -1; j <= 1; j++)
    for (int i = -1; i <= 1; i++)
    {
        vec2 g = vec2(float(i), float(j));
        vec2 r = crackPoint(n + g, t) - qp;
        float d = dot(r, r);
        if (d < md) { md = d; mr = r; mg = g; }
    }
    outCell = n + mg;
    md = 8.0;
    for (int j = -2; j <= 2; j++)
    for (int i = -2; i <= 2; i++)
    {
        vec2 g = mg + vec2(float(i), float(j));
        vec2 r = crackPoint(n + g, t) - qp;
        if (dot(mr - r, mr - r) > 1e-5)
            md = min(md, dot(0.5 * (mr + r), normalize(r - mr)));
    }
    return md;
}

// The Reduced-quality neighbourhood: ONE 3x3 pass tracking the nearest two feature-point distances, border
// ~ (F2 - F1) * 0.5. Softer and occasionally wrong near cell corners, at a third of the point evaluations.
float crackBorderDistCheap(vec2 qp, float t, out vec2 outCell)
{
    vec2 n = floor(qp);
    float f1 = 8.0, f2 = 8.0;
    outCell = n;
    for (int j = -1; j <= 1; j++)
    for (int i = -1; i <= 1; i++)
    {
        vec2 g = vec2(float(i), float(j));
        float d = length(crackPoint(n + g, t) - qp);
        if (d < f1) { f2 = f1; f1 = d; outCell = n + g; }
        else if (d < f2) { f2 = d; }
    }
    return (f2 - f1) * 0.5;
}

void main() {
    // Build NDC from gl_FragCoord, NOT from an interpolated UV: render-target texture SAMPLING has a
    // backend-dependent Y origin (the backend does not normalize it; the post passes hide this because they sample and
    // write at the same UV so any flip cancels, but a reconstruction does not), whereas gl_FragCoord is upper-left
    // on every backend. Both paths below unproject with the RAW (un-clip-corrected) inverse view-projection,
    // matching the backend-independent Camera.ScreenToRay picking convention, so the decal is identical on
    // Metal/D3D11/Vulkan.
    ivec2 sz = textureSize(sampler2D(DepthTex, Samp), 0);
    vec2 ndcXY = vec2(gl_FragCoord.x / float(sz.x) * 2.0 - 1.0, 1.0 - gl_FragCoord.y / float(sz.y) * 2.0);
    vec3 world = vec3(0.0);
    bool onPlane = false;   // this fragment paints the VIRTUAL plane rather than a real surface, so VoidDim applies

    // WHICH SURFACE THIS FRAGMENT PAINTS. Extra.y says which PIPELINE drew this instance, and the two are disjoint
    // by hardware: base instances draw with a Greater depth test at z=1 (passes on GEOMETRY only), void instances
    // with the exact complement, an Equal test at z=1 (passes on BACKGROUND only, where the stored depth still
    // equals the cleared far plane). That hardware split is not a nicety: the depth-colour target is CLEARED TO THE
    // BACKGROUND COLOUR, not to the far plane, so its .r at a background pixel is an arbitrary value and the shader
    // cannot tell background from geometry on its own. Only the depth test can.
    if (Extra.y > 0.5) {
        // BACKGROUND (flagged decals only). Nothing is drawn here at all, so the decal's own plane is visible
        // wherever the ray reaches it. No depth comparison is possible or needed.
        vec3 hit, ro, rd; float t;
        if (!planeHit(ndcXY, Center.y, hit, t, ro, rd)) discard;
        world = hit;
        onPlane = true;
        // No Y-band gate: the hit IS on the decal's plane by construction, so the gate is a tautology that only
        // float error at grazing angles could fail.
    } else {
        // GEOMETRY. Reconstruct the real surface world position from the stored depth. Sample by integer pixel
        // (texelFetch at gl_FragCoord). Background pixels never reach here, the hardware Greater test rejected them.
        float depth = texelFetch(sampler2D(DepthTex, Samp), ivec2(gl_FragCoord.xy), 0).r;
        // Dynamic-geometry reject (issue #235). The model pass tags skinned/dynamic surfaces with normal-target
        // alpha 0 (the static world writes 1). When the caller enables it (TimeQ.w, set by the MAIN decal pass; the
        // early blob-shadow pass leaves it 0, so this never touches its not-yet-resolved normal target), discard the
        // tagged pixels so a ground decal never paints onto a character standing in its Y-band. Reject only near-zero
        // alpha: any static coverage in the pixel keeps the resolved alpha at least 1/samples (>= 0.125 at 8x MSAA,
        // the maximum), so a scene with no skinned geometry never trips it - byte-identical, MSAA silhouettes included.
        if (TimeQ.w > 0.5 && texelFetch(sampler2D(NormalTex, Samp), ivec2(gl_FragCoord.xy), 0).a < 0.03) discard;
        vec4 wp = InvViewProj * vec4(ndcXY, depth, 1.0);
        vec3 g = wp.xyz / wp.w;

        // Y-band gate: only conform to surfaces near the decal's ground height (terrain, not walls).
        float gateLo = Gate.x - Gate.y;
        float gateHi = Gate.x + Gate.z;
        bool isGround = (g.y >= gateLo && g.y <= gateHi);

        // The band alone CANNOT tell a terrain dip YTolerance below the plane from the TOP YTolerance of a vertical
        // cliff face - at one pixel, with only depth, those are the same number. Conforming onto the latter runs the
        // decal down the cliff (evaluated at the cliff's XZ, pinned at the edge) instead of leaving it flat, which is
        // exactly wrong for a decal whose whole point is to be a flat disc at its own height. The geometric normal is
        // the only thing that separates them, so a fallback decal additionally requires a near-horizontal surface.
        // Gated on the flag: an unflagged decal never samples this and keeps the legacy band-only behaviour, wart and
        // all, so the zero-neutral contract holds. (That legacy wrap-down is a pre-existing artifact on any sharp
        // edge - see https://github.com/APKiwiOrg/KhaozEngine/issues/11.)
        if (isGround && Extra.w > 0.5) {
            vec3 nrm = texelFetch(sampler2D(NormalTex, Samp), ivec2(gl_FragCoord.xy), 0).xyz * 2.0 - 1.0;
            isGround = nrm.y >= GroundNormalMinY;
        }

        if (isGround) {
            world = g;                  // this decal's ground: today's exact path, byte-for-byte
        } else if (Extra.w > 0.5) {
            // NOT THIS DECAL'S GROUND, on a FALLBACK decal: either out of the Y band, or a wall/cliff face the
            // normal test just rejected. The decal's own plane may still be genuinely VISIBLE in front of it: a
            // ring overhanging a mesa's edge hangs at the top surface's height, so the cliff below and behind it
            // does not hide it. Whether it does is a DEPTH question, not a has-geometry question, so compare the
            // plane hit against this surface along the ray. Nearer wins. If the plane is behind (a wall standing on
            // the decal's ground, the decal passing under it), the geometry occludes it and we discard rather than
            // x-ray through solid.
            vec3 hit, ro, rd; float t;
            if (!planeHit(ndcXY, Center.y, hit, t, ro, rd)) discard;
            float tGeom = (g.y - ro.y) / rd.y;   // same ray, same basis, so the parameters are directly comparable
            if (t > tGeom) discard;              // plane is further along the ray than the surface: occluded
            world = hit;
            onPlane = true;
        } else {
            discard;                    // legacy: out of band and no fallback asked for
        }
    }

    // Into shape-local XZ (translate by center, rotate by -rotation so +x is the facing axis). Everything from
    // here down (SDF, feather, pattern, base fill, interior dim, energy lanes) is SHARED by both paths.
    vec2 q = world.xz - Center.xz;
    float c = cos(-Center.w), s = sin(-Center.w);
    vec2 local = vec2(q.x * c - q.y * s, q.x * s + q.y * c);

    int shape = int(Params.w + 0.5);
    float edge = max(Params.x, 1e-4);
    float fillFrac = clamp(Params.y, 0.0, 1.0);
    float sd;        // signed distance to the shape boundary (negative inside)
    float swept;     // signed distance to the swept (animated) fill boundary
    float halfDim;   // approximate half-thickness (boundary to medial axis), the edge-erosion depth reference

    if (shape == 0) {              // Circle: Size.x = radius
        sd = sdCircle(local, Size.x);
        swept = sdCircle(local, Size.x * fillFrac);
        halfDim = Size.x;
    } else if (shape == 1) {       // Ring: Size.x=innerR, Size.y=outerR
        sd = sdRing(local, Size.x, Size.y);
        swept = sdRing(local, Size.x, Size.x + (Size.y - Size.x) * fillFrac);
        halfDim = (Size.y - Size.x) * 0.5;
    } else if (shape == 2) {       // Beam: Size.x=halfLength, Size.y=halfWidth (origin at one end -> shift by halfLength)
        vec2 b = vec2(Size.x, Size.y);
        vec2 p = local - vec2(Size.x, 0.0);
        sd = sdBox(p, b);
        swept = sdBox(p, vec2(Size.x * fillFrac, Size.y));
        halfDim = Size.y;
    } else if (shape == 3) {       // Cone: Size.x=range, Size.y=halfAngle. Sector via radius + angle test.
        float ang = atan(local.y, local.x);
        float inAng = abs(ang) - Size.y;             // <=0 inside the angular wedge
        float inRad = length(local) - Size.x;        // <=0 inside the range
        sd = max(inRad, inAng);
        swept = max(length(local) - Size.x * fillFrac, inAng);
        halfDim = Size.x * 0.5;
    } else {                       // Arc: Size.x=radius, Size.y=halfBandWidth, Size.z=startAngle, Size.w=sweep
        float ang = atan(local.y, local.x) - Size.z;
        ang = mod(ang + 6.2831853, 6.2831853);       // 0..2pi from start
        float band = abs(length(local) - Size.x) - Size.y;
        float halfSweep = Size.w * 0.5;
        float inAng = abs(ang - halfSweep) - halfSweep;  // <=0 within [0, sweep]
        sd = max(band, inAng);
        float sweptHalf = (Size.w * fillFrac) * 0.5;
        swept = max(band, abs(ang - sweptHalf) - sweptHalf);
        halfDim = Size.y;
    }

    // Edge erosion (Misc.y, 0 = the exact analytic boundary, gated so it is zero-neutral): bite the boundary
    // INWARD by up to 35% of the shape's half-thickness, modulated by STABLE value noise in decal-local space (no
    // time term, no RNG - the silhouette is identical frame to frame and across clients). Equivalent to the
    // margin form: a pixel at depth d inside the band survives iff noise > 1 - d/bite, a threshold rising toward
    // the analytic edge, so the smooth boundary breaks into organic fingers. Inward-only on purpose: the CPU-side
    // footprint quad is sized to the analytic bounds, so an outward push would clip at the quad edge. Both sd and
    // swept shift by the same field, so the fill, its sweep front, the outline band, and every boundary-anchored
    // energy lane follow the eroded silhouette. Feather then softens the survivors (erode first, then feather).
    // Reduced quality drops the second octave, like the fill patterns.
    float ero = Misc.y;
    if (ero > 0.0)
    {
        float en = vnoise(local * 2.7 + 31.7);
        if (TimeQ.y > 0.5) en = 0.65 * en + 0.35 * vnoise(local * 6.1 + 7.9);
        float bite = ero * 0.35 * max(halfDim, 0.0) * (1.0 - en);
        sd += bite;
        swept += bite;
    }

    // Feathered coverage. feather (Gate.w, world units) softens both boundaries. ZERO-NEUTRAL: with feather == 0,
    // smoothstep(-0.0, edge, swept) == smoothstep(0.0, edge, swept) and (edge * 2.0 + 0.0) == edge * 2.0, so these are
    // IEEE-identical to the legacy hard-edge lines - the committed telegraph_ground goldens depend on that.
    float feather = max(Gate.w, 0.0);
    // Fill: inside the swept boundary, AA across one edge width (widened by feather). cover is the Fill.a-free
    // shape coverage - MoltenCracks keys its crack alpha off it so the cracks stay independent of the field alpha.
    float cover = 1.0 - smoothstep(-feather, edge + feather, swept);
    float fillA = cover * Fill.a;
    // Outline: a band straddling the FULL shape boundary. The feather contribution is halved so soft styles do
    // not grow fat borders (feather == 0 keeps the exact legacy band, the zero-neutral contract).
    float outlineA = (1.0 - smoothstep(edge, edge * 2.0 + feather * 0.5, abs(sd))) * Outline.a;

    // Base fill (Extra.x, 0 = legacy, gated so it is zero-neutral): a faint tint across the ENTIRE shape from
    // progress 0, independent of the sweep. This is what lets a borderless (FillMode.Fill) telegraph read its
    // full danger extent immediately, the sweep then brightens across it.
    if (Extra.x > 0.0)
    {
        float baseCover = 1.0 - smoothstep(-feather, edge + feather, sd);
        float baseA = baseCover * Fill.a * Extra.x;
        fillA = max(fillA, baseA);
        cover = max(cover, baseCover * Extra.x);
    }

    // Animated fill patterns. MoltenCracks (3) paints a two-tone field of its own. The noise variants (1, 2)
    // modulate fillA exactly as before. Gated on the pattern index (Solid == 0 touches nothing, so zero-neutral).
    // Reduced quality (TimeQ.y <= 0.5) drops the second octave / the exact Voronoi neighbourhood.
    vec3 fillRgb = Fill.rgb;
    float patIdx = PatternP.x;
    if (patIdx > 2.5 && cover > 0.0)
    {
        // MoltenCracks: an animated Voronoi crack web in decal-local XZ. Edge distance to the cell borders maps
        // through a heat ramp: a thin near-white core AT the border, an Accent-coloured glow falling off around
        // it, and the dark Fill colour field between cells. The field alpha rides Fill.a (near-opaque scorch)
        // while the crack alpha rides cover * Accent.a, so each is authorable independently. FlashAdd still
        // lifts everything at the end of the shader as the global pulse hook.
        float cells = PatternP.z > 0.0 ? PatternP.z : 1.0;
        float t = TimeQ.x * PatternP.y;
        vec2 qp = local * cells;
        vec2 cellId;
        float bd = TimeQ.y > 0.5 ? crackBorderDist(qp, t, cellId) : crackBorderDistCheap(qp, t, cellId);
        float w = Misc.x > 0.0 ? Misc.x : 0.22;           // crack width, cell-space units (PatternParam)
        // Slow per-cell heat swell on top of the point drift: [0.78, 1.0], breathing, never blinking off.
        float pulse = 0.89 + 0.11 * sin(6.2831853 * (t + hash21(cellId + 7.3)));
        float glow = (1.0 - smoothstep(0.0, w, bd)) * pulse;
        float core = 1.0 - smoothstep(0.0, w * 0.35, bd);
        // The core lifts toward white on top of the Accent tint, then over-drives: LDR clamps it to near-white,
        // HDR carries the over-range energy into bloom (the TimeQ.z ceiling below).
        vec3 crackRgb = mix(Accent.rgb, vec3(1.0), 0.75 * core) * (1.0 + 1.5 * core * pulse);
        fillRgb = mix(Fill.rgb, crackRgb, clamp(glow, 0.0, 1.0));
        fillA = max(fillA, cover * Accent.a * glow);
    }
    else if (patIdx > 0.5 && fillA > 0.0)
    {
        float cells = PatternP.z > 0.0 ? PatternP.z : 1.0;
        float t = TimeQ.x * PatternP.y;
        float n;
        if (patIdx < 1.5)
        {
            // ScrollingNoise: domain-warped value noise drifting across the decal-local XZ plane. The warp
            // vector (itself low-frequency noise) bends the drift into wispy filaments instead of round blobs.
            vec2 qp = local * cells;
            vec2 drift = vec2(t, t * 0.7);
            vec2 warp = vec2(vnoise(qp * 0.55 + drift * 0.6),
                             vnoise(qp * 0.55 - drift * 0.4 + 17.3)) - 0.5;
            n = vnoise(qp + warp * 2.6 + drift);
            if (TimeQ.y > 0.5)
                n = 0.62 * n + 0.38 * vnoise(qp * 2.3 + warp * 3.5 - vec2(t * 1.3, -t));
        }
        else
        {
            // RadialNoise: vortex swirl. Rotate the sample domain by an angle growing with radius (spiral
            // arms) and let the arms orbit over time. Sampling stays Cartesian, so there is no polar
            // singularity mushing the shape center (the old radius/angle sampling compressed all angular
            // cells into a blob at r -> 0).
            float rr = length(local);
            float twist = rr * cells * 0.5 - t * 1.6;
            float cs2 = cos(twist), sn2 = sin(twist);
            vec2 sp = vec2(local.x * cs2 - local.y * sn2, local.x * sn2 + local.y * cs2) * cells;
            n = vnoise(sp + vec2(0.0, t * 0.9));
            if (TimeQ.y > 0.5)
                n = 0.65 * n + 0.35 * vnoise(sp * 2.1 - vec2(t * 1.1, t * 0.5));
        }
        // Filament contrast: dark gaps between bright energy wisps, not a milky uniform modulation.
        float filaments = smoothstep(0.35, 0.75, n);
        fillA *= 0.35 + 0.95 * filaments;
    }

    // Hollow interior (PatternP.w = interiorDim, 0 = legacy uniform fill, gated so it is zero-neutral). Alpha
    // eases down deep inside the swept region while staying full within a band of the sweep front, so the
    // energy reads at the rim and the moving edge instead of pooling into a ball at the shape center.
    if (PatternP.w > 0.0 && fillA > 0.0)
    {
        float hollowBand = edge * 3.0 + feather * 2.0;
        float depthIn = clamp(-swept / max(hollowBand, 1e-4), 0.0, 1.0);
        fillA *= 1.0 - PatternP.w * depthIn * depthIn;
    }

    vec3 rgb = fillRgb;   // Fill.rgb for every pattern but MoltenCracks, which paints its two-tone field into it
    float a = fillA;
    // Composite the outline over the fill.
    rgb = mix(rgb, Outline.rgb, outlineA <= 0.0 ? 0.0 : outlineA / max(outlineA + fillA, 1e-4));
    a = max(a, outlineA);

    // Edge energy. Each term is gated by its own Energy lane, so a zero lane is arithmetically inert and the whole
    // block is a no-op when Energy == 0 (the trailing clamp is an identity on an already-in-range rgb) - zero-neutral.
    if (Energy.x > 0.0)
    {
        // Rim glow: a band straddling the full boundary, tinted toward the outline colour with a slow shimmer.
        float rim = (1.0 - smoothstep(0.0, edge * 1.5 + feather * 0.5, abs(sd))) * Energy.x;
        float shimmer = 0.85 + 0.15 * sin(TimeQ.x * 6.0 + Center.x + Center.z);
        rgb = mix(rgb, Outline.rgb, clamp(rim * 0.6, 0.0, 1.0));
        a = max(a, rim * shimmer * Outline.a * 0.8);
    }
    if (Energy.y > 0.0)
    {
        // Sweep glow: a leading-edge glow tracking the animated (swept) fill boundary. The band is kept to
        // roughly one edge-plus-feather width so an early-cast swept region is never fully engulfed (the
        // resolver additionally ramps the energy in over the first fifth of the cast).
        float lead = 1.0 - smoothstep(0.0, edge * 2.0 + feather, abs(swept));
        rgb += Outline.rgb * (lead * Energy.y * 0.7);
        a = max(a, lead * Energy.y * 0.6 * Fill.a);
    }
    if (Energy.z > 0.0 && TimeQ.y > 0.5)
    {
        // Edge sparkle: brief per-cell twinkles along the boundary (Full quality only). Small cells and a
        // smoothstepped threshold give soft glints rather than hard square flecks.
        float bmask = 1.0 - smoothstep(0.0, edge * 3.0 + feather, abs(sd));
        vec2 cell = floor(local * 11.0);
        float ph = hash21(cell + floor(TimeQ.x * 7.0));
        float tw = smoothstep(0.94, 0.995, ph);
        rgb += vec3(1.0) * (bmask * tw * Energy.z);
        a = max(a, bmask * tw * Energy.z * 0.9);
    }
    if (Energy.w > 0.0)
    {
        // Outline runner: eight soft dash segments orbiting the outline band (rune-ring feel). Angular dashes
        // are shape-agnostic: on radial shapes they orbit, on beams they stride along the length. The band mask
        // keeps them strictly on the boundary, so the shape center is untouched.
        float oband = 1.0 - smoothstep(edge, edge * 2.0 + feather, abs(sd));
        float seg = fract(atan(local.y, local.x) * 1.2732395 + TimeQ.x * 0.45);
        float dash = smoothstep(0.32, 0.42, seg) * (1.0 - smoothstep(0.78, 0.88, seg));
        rgb = mix(rgb, Outline.rgb, clamp(oband * dash * Energy.w * 0.85, 0.0, 1.0));
        a = max(a, oband * dash * Energy.w * Outline.a);
    }
    // Ceiling is TimeQ.z: 1.0 in LDR (bit-identical to the legacy clamp), 65504.0 (float16 max) in HDR so the
    // energy lanes can push telegraph cores over 1.0 and bloom before the tonemap compresses them.
    rgb = clamp(rgb, 0.0, TimeQ.z);

    // Impact flash: brighten toward white. Kept exactly where the legacy shader had it.
    rgb = clamp(rgb + Params.z, 0.0, TimeQ.z);

    // Void dim (Extra.z), applied to PLANE-projected pixels only, so a projection can read as projected rather than
    // as standing on ground. Both plane paths set onPlane, and an unflagged decal can never reach either, so this is
    // zero-neutral. Applied BEFORE the discard test so a fully dimmed (Extra.z = 1) plane pixel drops out entirely.
    if (onPlane) a *= 1.0 - Extra.z;
    if (a <= 0.001) discard;
    // Edge-energy lanes (rim/sweep glow, max-composited) can push a above 1 on float render targets. Legacy
    // decals (all-new lanes zero) already keep a = max(fillA, outlineA) in [0,1], so this clamp is an identity
    // for them and only bites the modern energy path.
    a = clamp(a, 0.0, 1.0);
    oColor = vec4(rgb, a);
}";
    }
}
