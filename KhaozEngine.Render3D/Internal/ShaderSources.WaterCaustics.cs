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
    /// <b>Energy.</b> A sheet's fragment adds the area ratio between the still-water patch and the map texel it landed
    /// on, so the term has mean 1 below the clamp. The subtract quad runs first because the map clears to 1: without it a
    /// rectangle's light would sit on top of that 1. No culling and additive blending let folded triangles add.
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
        /// Sheet vertex stage. One instance is one patch at one slice. Reads the field at the still-water lattice point,
        /// refracts the sun through its normal and places the vertex where that ray lands at the slice depth, relative to
        /// the still ray so strength scales deflection and nothing else. <c>S</c> is clamped to the rectangle so patches
        /// overhanging it collapse to zero area and add nothing.
        /// </summary>
        internal const string WaterCausticSheetVert = @"#version 450
" + WaterCausticBodyGlsl + WaterCausticClipGlsl + WaterRefractionShaderSource.RefractiveIndexGlsl + @"
layout(set=0, binding=1) uniform texture2D SurfaceField;
layout(set=0, binding=2) uniform sampler SurfaceFieldSampler;

layout(location=0) in vec2 Local;      // patch-local lattice coordinates, 0 to 32
layout(location=1) in vec3 Instance;   // latticeX, latticeZ, slice
layout(location=0) out vec2 vS;
layout(location=1) flat out vec4 vSliceMask;

void main() {
    float cell = CausticWindow.w;
    vec2 windowMin = CausticWindow.xy;
    vec2 lattice = Instance.xy + Local;
    vec2 S = clamp(windowMin + lattice * cell, CausticRect.xy, CausticRect.zw);

    vec2 texel = ((S - windowMin) / cell + 0.5) / KE_FIELD_TEXELS;
    vec4 f = textureLod(sampler2D(SurfaceField, SurfaceFieldSampler),
                        vec2((CausticAtlas.x + texel.x) / CausticAtlas.y, texel.y), 0.0);

    vec3 n = normalize(vec3(f.z, 1.0, f.w));
    vec3 t = refract(SunTravel.xyz, n, 1.0 / UnderwaterRefractiveIndex);
    vec4 sliceMask = vec4(equal(ivec4(int(Instance.z + 0.5)), ivec4(0, 1, 2, 3)));
    float sliceDepth = StillRay.w * dot(sliceMask, vec4(0.125, 0.25, 0.5, 1.0));
    vec2 land = S + f.xy + SunTravel.w * sliceDepth
        * (t.xz / max(-t.y, 0.1) - StillRay.xz / max(-StillRay.y, 0.1));

    vS = S;
    vSliceMask = sliceMask;
    gl_Position = causticClip((land - windowMin) * CausticWindow.z);
}";

        /// <summary>Sheet fragment stage: the still-water area this map texel gathered, over the texel's own area
        /// (0.0625 m per side), added to the sheet's slice. The clamp keeps a fold's hot line finite.</summary>
        internal const string WaterCausticSheetFrag = @"#version 450
layout(location=0) in vec2 vS;
layout(location=1) flat in vec4 vSliceMask;
layout(location=0) out vec4 oMap;

void main() {
    oMap = vSliceMask * min(abs(determinant(mat2(dFdx(vS), dFdy(vS)))) / (0.0625 * 0.0625), 16.0);
}";
    }
}
