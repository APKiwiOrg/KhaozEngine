namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The GLSL blocks that turn the water pair into an FFT-ocean reader (<see cref="WaterWaveSource.FftOcean"/>):
    /// the two map bindings, and the cascade summation each stage splices into its own <c>main</c>. Part of the
    /// <see cref="ShaderSources"/> partial; ShaderSources.Water.cs owns the surface itself and splices these in.
    /// <para>
    /// Split out because the two wave sources are genuinely separate concerns sharing one shading stack. Keeping
    /// the FFT reader here means the procedural surface's source reads exactly as it did in 14.28.0, and the FFT
    /// branch is one clearly-bounded block in each stage rather than a diff smeared through both.
    /// </para>
    /// <para>
    /// <b>Two Metal-only landmines shape everything below.</b> Neither is visible in the GLSL, and between them
    /// they are why the ocean is ONE texture bound FIRST rather than two textures bound last.
    /// </para>
    /// <para>
    /// First, Veldrid numbers a backend's resource slots with one counter PER KIND across the whole resource
    /// layout, and binds each element to the stages in its mask - while the cross-compiler numbers each stage
    /// DENSELY over only the bindings that stage declares. The two agree only when every stage's resources are a
    /// PREFIX of the layout. A vertex-only texture sitting after a fragment-only one therefore cannot line up at
    /// any binding number: the vertex sees dense index 0 and Veldrid binds it at global index 1, so the vertex
    /// samples an unbound slot and gets zero, silently. Hence one ocean map array, declared identically in both
    /// stages, ahead of the fragment-only scene depth - the vertex's resources are then exactly the first entry of
    /// each kind, and the fragment's are the first two.
    /// </para>
    /// <para>
    /// Second, within a stage that dense numbering follows FIRST REFERENCE across the emitted function bodies, and
    /// a function is emitted before <c>main</c>. A tidy <c>oceanSurface()</c> helper reached the ocean map before
    /// <c>main</c> touched the scene depth and swapped the two, so the water read its own derivative layer as the
    /// depth buffer. Both stages therefore sample INSIDE <c>main</c>, ocean first, which is the same
    /// first-sample-order rule ModelFrag and SplatFrag already carry with the extra twist that a helper function
    /// jumps the queue. The fragment's ocean block sits inside its <c>if</c> so Procedural mode still pays nothing
    /// at runtime; emission order is a static property and the branch does not affect it.
    /// </para>
    /// </summary>
    internal static partial class ShaderSources
    {
        /// <summary>Maximum cascades the water shaders can sum, mirroring
        /// <see cref="OceanSpectrum.MaxCascades"/> and the <c>Cascade[3]</c> array in the compute kernels.</summary>
        internal const int WaterMaxCascades = 3;

        // Declared IDENTICALLY in both stages, and FIRST in the set, both deliberately (see the class note).
        // Layers [0, cascadeCount) are displacement, [cascadeCount, 2*cascadeCount) are derivatives.
        const string WaterFftBindingsGlsl = @"layout(set=0, binding=0) uniform texture2DArray OceanMap;
layout(set=0, binding=1) uniform sampler OceanSamp;   // WRAPPING bilinear: each cascade tiles at its own period
";

        // Shared by both stages. Touches no resource, so it may safely live in a function: only the SAMPLING has to
        // stay inside main. A switch rather than FftTiles[i], because dynamic indexing into a vector cross-compiles
        // to a scratch array on some backends and this is two selects.
        const string WaterFftCommonGlsl = @"
const int KE_MAX_CASCADES = 3;

float oceanTile(int i) { return i == 0 ? FftTiles.x : (i == 1 ? FftTiles.y : FftTiles.z); }
float oceanVariance(int i) { return i == 0 ? FftVariance.x : (i == 1 ? FftVariance.y : FftVariance.z); }

// Half-texel offset so a world position lands on a texel CENTRE rather than on the boundary between two: the
// compute kernel writes texel (px, pz) for world (px, pz) * tile / resolution.
vec2 oceanUv(vec2 xz, float tile, float halfTexel) { return xz / tile + halfTexel; }
";

        /// <summary>
        /// Vertex stage, spliced INTO main: sum every cascade's displacement at the still-water position into
        /// <c>oceanDisp</c>. Sampled with <c>textureLod</c> because a vertex shader has no derivatives to pick a
        /// mip with, and the maps are single-mip anyway.
        /// </summary>
        const string WaterFftVertGlsl = @"
        int nc = clamp(int(FftParams.y + 0.5), 1, KE_MAX_CASCADES);
        float halfTexel = 0.5 / max(FftParams.z, 1.0);
        vec3 oceanDisp = vec3(0.0);
        for (int i = 0; i < KE_MAX_CASCADES; i++) {
            if (i >= nc) break;
            vec2 uv = oceanUv(Position.xz, oceanTile(i), halfTexel);
            oceanDisp += textureLod(sampler2DArray(OceanMap, OceanSamp), vec3(uv, float(i)), 0.0).xyz;
        }
";

        /// <summary>
        /// Fragment stage, spliced INTO main ahead of the scene-depth fetch and inside the FFT branch: sum the
        /// cascades' slope into <c>oceanSlope</c>, take the strongest foam into <c>oceanFoam</c>, and accumulate
        /// into <c>oceanLost</c> the slope variance the footprint band-limit removed, so the glint lobe can absorb
        /// it. All three are declared by the caller, so this block can live inside the branch.
        /// <para>
        /// The band-limit is the SAME measure the procedural spectrum uses (<c>rippleResolve</c> against the pixel
        /// footprint), applied per cascade against twice its texel size, which is the shortest wave that cascade
        /// can carry. It has to be here: a 128-texel cascade over a 14 metre tile is 11 cm of detail, and past the
        /// distance where a pixel covers that, it is noise that crawls. Foam is deliberately NOT band-limited,
        /// because it is the one channel whose far-field read should survive - a whitecap two kilometres out is
        /// still white.
        /// </para>
        /// <para>
        /// The removed variance comes from the per-cascade slope variance baked with the spectrum, not from the
        /// sampled slope: the sampled value is one realization at one texel, and Toksvig wants the statistic.
        /// </para>
        /// </summary>
        const string WaterFftFragGlsl = @"
        int nc = clamp(int(FftParams.y + 0.5), 1, KE_MAX_CASCADES);
        float res = max(FftParams.z, 1.0);
        float halfTexel = 0.5 / res;
        for (int i = 0; i < KE_MAX_CASCADES; i++) {
            if (i >= nc) break;
            float tile = oceanTile(i);
            // Derivative layers follow the displacement layers, so cascade i is at nc + i.
            vec4 d = textureLod(sampler2DArray(OceanMap, OceanSamp),
                                vec3(oceanUv(vRefXz, tile, halfTexel), float(nc + i)), 0.0);
            float keep = rippleResolve(2.0 * tile / res, footprint, footprintSamples);
            oceanSlope += d.xy * keep;
            oceanFoam = max(oceanFoam, d.z);
            oceanLost += oceanVariance(i) * (1.0 - keep * keep);
        }
";
    }
}
