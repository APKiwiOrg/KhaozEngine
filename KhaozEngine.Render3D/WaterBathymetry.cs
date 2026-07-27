using System;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A consumer-supplied WATER DEPTH field over a world-space XZ rectangle: the input that lets the surface know
    /// where the sea is shallow, so the swell can calm as it comes in (<see cref="WaterSettings.ShoalingStrength"/>)
    /// and can break where it runs out of water (<see cref="WaterSettings.SurfStrength"/>). Hand it to
    /// <see cref="WaterSettings.Bathymetry"/>; leaving that null is the default and leaves the surface exactly what
    /// it was before this existed.
    /// <para>
    /// <b>It is a plain CPU array, deliberately.</b> Every consumer already has the ground height somewhere - a
    /// terrain field, a heightmap, a collision mesh - and none of them have it as a GPU texture in a format the
    /// water pass could read. Asking for an <c>IGpuTexture</c> would push the format, the usage flags and the
    /// upload into each game; asking for depths in metres pushes nothing. The renderer owns the texture, uploads on
    /// a <see cref="Revision"/> change and never re-uploads otherwise, so a field that is baked once at load costs
    /// one upload for the life of the process.
    /// </para>
    /// <para>
    /// <b>What a value MEANS.</b> <see cref="Depths"/>[z * <see cref="Resolution"/> + x] is the water depth in
    /// metres at that texel's world position: still-water surface height minus ground height. Positive is water,
    /// zero or negative is land. It is a depth below the SURFACE, not a ground elevation, so a consumer whose
    /// water sits at y = 0 writes <c>-groundY</c>, and one whose sea level is 12 writes <c>12 - groundY</c>. The
    /// rect need not cover the whole plane: outside it the surface reads as deep open water and nothing is
    /// attenuated, which is what makes it affordable to bake a coastal strip at a useful resolution instead of an
    /// ocean at a useless one.
    /// </para>
    /// <para>
    /// <b>Resolution is a shore-detail knob, not a quality one.</b> The field is sampled bilinearly and only ever
    /// drives low-frequency behaviour (a depth taper and a band edge), so it wants to resolve the SHAPE of the
    /// coast rather than its texture. A texel every few metres reads well; a texel every few centimetres buys
    /// nothing and costs the upload. What it must not do is miss a feature the surf is meant to wrap: a rock that
    /// falls between two texels is a rock the surf breaks straight through.
    /// </para>
    /// </summary>
    public sealed class WaterBathymetry
    {
        /// <summary>Smallest field the renderer will build a texture for.</summary>
        public const int MinResolution = 2;

        /// <summary>Largest field the renderer will build a texture for. 1024 squared at 8 bytes a texel is 8 MB,
        /// well past what a low-frequency depth field needs; the cap is a guard, not a recommendation.</summary>
        public const int MaxResolution = 1024;

        /// <summary>Build an empty field of <paramref name="resolution"/> texels per side covering the world-space
        /// rectangle centred on (<paramref name="centerX"/>, <paramref name="centerZ"/>).</summary>
        /// <param name="resolution">Texels per side, clamped to
        /// <see cref="MinResolution"/>..<see cref="MaxResolution"/>.</param>
        /// <param name="centerX">World X of the rectangle's centre.</param>
        /// <param name="centerZ">World Z of the rectangle's centre.</param>
        /// <param name="halfExtentX">Half the rectangle's world width.</param>
        /// <param name="halfExtentZ">Half the rectangle's world depth; 0 or less mirrors
        /// <paramref name="halfExtentX"/> (a square), matching <see cref="WaterPlane"/>'s convention.</param>
        public WaterBathymetry(int resolution, float centerX, float centerZ, float halfExtentX,
            float halfExtentZ = 0f)
        {
            Resolution = Math.Clamp(resolution, MinResolution, MaxResolution);
            CenterX = centerX;
            CenterZ = centerZ;
            HalfExtentX = MathF.Max(halfExtentX, 1e-3f);
            HalfExtentZ = halfExtentZ > 0f ? halfExtentZ : HalfExtentX;
            Depths = new float[Resolution * Resolution];
        }

        /// <summary>Texels per side. Fixed at construction: the renderer keys its texture on it.</summary>
        public int Resolution { get; }

        /// <summary>World X of the covered rectangle's centre.</summary>
        public float CenterX { get; }

        /// <summary>World Z of the covered rectangle's centre.</summary>
        public float CenterZ { get; }

        /// <summary>Half the covered rectangle's world width.</summary>
        public float HalfExtentX { get; }

        /// <summary>Half the covered rectangle's world depth.</summary>
        public float HalfExtentZ { get; }

        /// <summary>Water depth in metres per texel, row-major as <c>[z * Resolution + x]</c>. Positive is water,
        /// zero or less is land. Write it directly (or through <see cref="FillFromGround"/>) and then call
        /// <see cref="MarkChanged"/>.</summary>
        public float[] Depths { get; }

        /// <summary>Bumped by <see cref="MarkChanged"/>. The renderer re-uploads when it sees a different value and
        /// does nothing at all otherwise, so a static coastline costs one upload ever.</summary>
        public int Revision { get; private set; }

        /// <summary>World size of one texel on X: the spacing the surf band's up-slope difference is taken
        /// over.</summary>
        public float TexelSizeX => 2f * HalfExtentX / Resolution;

        /// <summary>World size of one texel on Z.</summary>
        public float TexelSizeZ => 2f * HalfExtentZ / Resolution;

        /// <summary>Tell the renderer the contents changed. Cheap, and required: without it a rewritten
        /// <see cref="Depths"/> never reaches the GPU.</summary>
        public void MarkChanged() => Revision++;

        /// <summary>World X of a texel's centre.</summary>
        public float WorldX(int x) => CenterX - HalfExtentX + (x + 0.5f) * TexelSizeX;

        /// <summary>World Z of a texel's centre.</summary>
        public float WorldZ(int z) => CenterZ - HalfExtentZ + (z + 0.5f) * TexelSizeZ;

        /// <summary>
        /// Fill every texel from a ground-height sampler and a still-water surface height, and
        /// <see cref="MarkChanged"/>. This is the whole adoption path for a consumer that already has a terrain
        /// field: pass its height function and the plane's <see cref="WaterPlane.SurfaceY"/>.
        /// </summary>
        /// <param name="groundHeight">World ground height at an (x, z). Called once per texel.</param>
        /// <param name="surfaceY">Still-water surface height the depths are measured down from.</param>
        public void FillFromGround(Func<float, float, float> groundHeight, float surfaceY)
        {
            ArgumentNullException.ThrowIfNull(groundHeight);
            for (int z = 0; z < Resolution; z++)
            {
                float wz = WorldZ(z);
                for (int x = 0; x < Resolution; x++)
                    Depths[z * Resolution + x] = surfaceY - groundHeight(WorldX(x), wz);
            }
            MarkChanged();
        }
    }
}
