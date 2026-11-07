namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The underwater caustic passes: the surface field, the flat quad that subtracts a rectangle's unit of light from
    /// the map, and the sun-view sheets that add it back wherever the refracted rays land. Part of the
    /// <see cref="ShaderSources"/> partial.
    /// <para>
    /// <b>Atlas mapping.</b> Both atlases hold one square per layer, side by side, and <c>CausticAtlas.y</c> is the atlas
    /// layer capacity rather than the frame's used count, because the atlas width follows the capacity. A square's
    /// <c>(u, v)</c> reaches clip space through <c>causticClip</c>, on both the field and the map.
    /// </para>
    /// <para>
    /// <b>The field lattice.</b> Texel <c>i</c> holds lattice point <c>i</c>, read at <c>(i + 0.5) / 512</c>, so a texel
    /// centre interpolates its lattice coordinate exactly. The vertex stage grows the CPU-clipped rectangle by one cell,
    /// because the sheets read bilinearly at a rectangle edge that need not sit on the lattice and both neighbouring
    /// texels have to hold this body's waves.
    /// </para>
    /// <para>
    /// <b>Energy.</b> Every texel a sheet triangle covers gains that triangle's still-water area over its landed area, so
    /// the term has mean 1 below the clamp. The ratio is exact per triangle and computed in the vertex stage rather than
    /// from screen derivatives, because on Metal a fragment's derivatives can mix neighbouring triangles depending on the
    /// draw's composition, so cutting a body into rectangles would change its bands. The subtract quad runs first because
    /// the map clears to 1: without it a rectangle's light would sit on top of that 1. No culling and additive blending let
    /// folded triangles add.
    /// </para>
    /// </summary>
    internal static partial class ShaderSources
    {
        // Every member is a vec4, so std140 needs no padding. The swell and ripple blocks read SwellParams, SwellShape and
        // RippleSpectrum by name, which is why these members keep the water block's names.
        const string WaterCausticBodyGlsl = @"layout(set=0, binding=0) uniform CausticBody {
    vec4 CausticWindow;    // minX, minZ, inverse extent, cell metres
    vec4 CausticAtlas;     // layer index, atlas layer capacity, clip Y sign, 0
    vec4 CausticRect;      // render-relative min XZ, max XZ
    vec4 SunTravel;        // normalized LightDir, strength
    vec4 StillRay;         // t0, D
    vec4 WaveParams;       // waveScale, waveSpeed, normalStrength, time
    vec4 DetailParams;     // warpStrength, unused
    vec4 SwellParams;
    vec4 SwellShape;
    vec4 RippleSpectrum;
    vec4 FootprintParams;  // samples per wavelength
    vec4 RenderOrigin;
};

const float KE_FIELD_TEXELS = 512.0;
";

        const string WaterCausticClipGlsl = @"
// A layer square's (u, v) in clip space.
vec4 causticClip(vec2 uv) {
    float atlasU = (CausticAtlas.x + uv.x) / CausticAtlas.y;
    return vec4(2.0 * atlasU - 1.0, (1.0 - 2.0 * uv.y) * CausticAtlas.z, 0.0, 1.0);
}
";

        // Six vertices make the rectangle, with no vertex buffer.
        const string WaterCausticQuadGlsl = @"
vec2 causticQuadCorner() {
    return vec2((0x32 >> gl_VertexIndex) & 1, (0x2C >> gl_VertexIndex) & 1);
}
";

        /// <summary>Surface field vertex stage: the grown rectangle in lattice coordinates.</summary>
        internal const string WaterSurfaceFieldVert = @"#version 450
" + WaterCausticBodyGlsl + WaterCausticClipGlsl + WaterCausticQuadGlsl + @"
layout(location=0) out vec2 vLattice;

void main() {
    float cell = CausticWindow.w;
    vec2 windowMin = CausticWindow.xy;
    vec2 low = max(CausticRect.xy - cell, windowMin);
    vec2 high = min(CausticRect.zw + cell, windowMin + 1.0 / CausticWindow.z);
    vec2 position = mix(low, high, causticQuadCorner());
    vLattice = (position - windowMin) / cell;
    gl_Position = causticClip((vLattice + 0.5) / KE_FIELD_TEXELS);
}";

        /// <summary>
        /// Surface field fragment stage. Evaluates the two positions the water pass uses, combines the normals as it does
        /// and writes <c>(entryOffset.xz, N.xz / N.y)</c>. The field is world space, so detail is 1, the footprint is the
        /// lattice cell and the swell shading attenuation does not apply. The entry offset carries the crest along the
        /// still-water ray to the still plane.
        /// </summary>
        internal const string WaterSurfaceFieldFrag = @"#version 450
" + WaterCausticBodyGlsl + WaterSwellCommonGlsl + WaterRippleCommonGlsl + @"
layout(location=0) in vec2 vLattice;
layout(location=0) out vec4 oField;

void main() {
    float cell = CausticWindow.w;
    float time = WaveParams.w;
    vec2 rel = CausticWindow.xy + vLattice * cell;
    vec2 absXz = rel + RenderOrigin.xz;

    vec3 offset, nSwell;
    float fold;
    gerstnerEvaluate(absXz, time, offset, nSwell, fold);
    vec3 slope = waterSlope(absXz + offset.xz, time, WaveParams.x, WaveParams.y, DetailParams.x, 1.0,
                            cell, FootprintParams.x);
    vec3 ripple = slopeToNormal(slope.x, slope.y, WaveParams.z);
    vec3 nSum = vec3(nSwell.x + ripple.x, nSwell.y, nSwell.z + ripple.z);
    vec3 N = normalize(nSum);

    oField = vec4(offset.xz + StillRay.xz * offset.y / -StillRay.y, N.xz / N.y);
}";

        /// <summary>Subtract vertex stage: the rectangle on the map. No half texel, because this is an area and not a
        /// lattice read.</summary>
        internal const string WaterCausticSubtractVert = @"#version 450
" + WaterCausticBodyGlsl + WaterCausticClipGlsl + WaterCausticQuadGlsl + @"
void main() {
    vec2 position = mix(CausticRect.xy, CausticRect.zw, causticQuadCorner());
    gl_Position = causticClip((position - CausticWindow.xy) * CausticWindow.z);
}";

        /// <summary>Subtract fragment stage: one unit off all four slices under additive blending.</summary>
        internal const string WaterCausticSubtractFrag = @"#version 450
layout(location=0) out vec4 oMap;

void main() {
    oMap = vec4(-1.0);
}";

        /// <summary>
        /// Sheet vertex stage, drawn without an index buffer: one instance is one patch at one slice, and each of a
        /// patch's 32 by 32 cells is six vertices, two triangles split from <c>(1, 0)</c> to <c>(0, 1)</c>. Every vertex lands
        /// all three corners of its triangle in the same fixed order, so the three vertices of a triangle compute a
        /// bit-identical area ratio and a shared corner lands identically wherever it appears. A corner reads the field
        /// at its still-water lattice point, refracts the sun through that normal and lands where the ray meets the slice
        /// depth, relative to the still ray so strength scales deflection and nothing else. <c>S</c> is clamped to the
        /// rectangle so patches overhanging it collapse to zero area and add nothing.
        /// </summary>
        internal const string WaterCausticSheetVert = @"#version 450
" + WaterCausticBodyGlsl + WaterCausticClipGlsl + WaterRefractionShaderSource.RefractiveIndexGlsl + @"
layout(set=0, binding=1) uniform texture2D SurfaceField;
layout(set=0, binding=2) uniform sampler SurfaceFieldSampler;

layout(location=0) in vec3 Instance;   // latticeX, latticeZ, slice
layout(location=0) flat out vec4 vDensity;

// The clamped still-water position of a lattice point and how far from it its ray lands at this slice depth.
vec2 causticCorner(vec2 lattice, float sliceDepth, out vec2 S) {
    float cell = CausticWindow.w;
    vec2 windowMin = CausticWindow.xy;
    S = clamp(windowMin + lattice * cell, CausticRect.xy, CausticRect.zw);

    // The window has 513 lattice points and 512 texels, and the +X edge must not read the next layer.
    vec2 texel = clamp(((S - windowMin) / cell + 0.5) / KE_FIELD_TEXELS,
                       vec2(0.5 / KE_FIELD_TEXELS), vec2((KE_FIELD_TEXELS - 0.5) / KE_FIELD_TEXELS));
    vec4 f = textureLod(sampler2D(SurfaceField, SurfaceFieldSampler),
                        vec2((CausticAtlas.x + texel.x) / CausticAtlas.y, texel.y), 0.0);

    vec3 n = normalize(vec3(f.z, 1.0, f.w));
    vec3 t = refract(SunTravel.xyz, n, 1.0 / UnderwaterRefractiveIndex);
    return f.xy + SunTravel.w * sliceDepth
        * (t.xz / max(-t.y, 0.1) - StillRay.xz / max(-StillRay.y, 0.1));
}

float causticCross(vec2 a, vec2 b) {
    return a.x * b.y - a.y * b.x;
}

void main() {
    int cell = gl_VertexIndex / 6;
    int role = gl_VertexIndex - cell * 6;
    vec2 cellLattice = Instance.xy + vec2(cell % 32, cell / 32);
    bool second = role >= 3;
    vec2 cornerA = cellLattice + (second ? vec2(1.0, 0.0) : vec2(0.0, 0.0));
    vec2 cornerB = cellLattice + vec2(0.0, 1.0);
    vec2 cornerC = cellLattice + (second ? vec2(1.0, 1.0) : vec2(1.0, 0.0));

    vec4 sliceMask = vec4(equal(ivec4(int(Instance.z + 0.5)), ivec4(0, 1, 2, 3)));
    float sliceDepth = StillRay.w * dot(sliceMask, vec4(0.125, 0.25, 0.5, 1.0));
    vec2 sa, sb, sc;
    vec2 da = causticCorner(cornerA, sliceDepth, sa);
    vec2 db = causticCorner(cornerB, sliceDepth, sb);
    vec2 dc = causticCorner(cornerC, sliceDepth, sc);

    // Edges relative to corner A, so the areas never subtract two large render-relative positions.
    vec2 stillB = sb - sa, stillC = sc - sa;
    float oldArea = abs(causticCross(stillB, stillC)) * 0.5;
    float newArea = abs(causticCross(stillB + (db - da), stillC + (dc - da))) * 0.5;
    // Dividing by no less than oldArea / 16 gives exactly the clamp for a landing collapsed to a line or a point, and
    // never forms an infinity or a zero divide, which Metal's fast math leaves undefined.
    float density = oldArea > 0.0 ? min(oldArea / max(newArea, oldArea * (1.0 / 16.0)), 16.0) : 0.0;
    vDensity = sliceMask * density;

    int corner = role - (second ? 3 : 0);
    vec2 land = corner == 0 ? sa + da : corner == 1 ? sb + db : sc + dc;
    gl_Position = causticClip((land - CausticWindow.xy) * CausticWindow.z);
}";

        /// <summary>Sheet fragment stage: the triangle's area ratio, added to its slice. The clamp keeps a fold's hot
        /// line finite.</summary>
        internal const string WaterCausticSheetFrag = @"#version 450
layout(location=0) flat in vec4 vDensity;
layout(location=0) out vec4 oMap;

void main() {
    oMap = vDensity;
}";
    }
}
