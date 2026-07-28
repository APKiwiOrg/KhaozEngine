using System;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The ONE definition of the dissolve noise's world-space scale, promoted out of the four shader sources that
    /// used to each spell it <c>6.0</c> inline (ModelFrag's rigid dissolve, ModelDissolveFrag and
    /// SkinnedModelDissolveFrag's character dissolve, and the shadow depth pass's dissolve fragment), plus the
    /// per-cascade rescale the SHADOW pass needs on top of it (issue #391).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The noise is a world-space value noise sampled at <c>dnoise(worldPos * scale)</c>, so its lattice cell is
    /// <c>1 / scale</c> world units across: at the <see cref="BaseScale"/> of 6 that is a ~16.7 cm cell, which is
    /// what the COLOUR passes want (a dissolve you can read at arm's length) and what they keep unchanged.
    /// </para>
    /// <para>
    /// The shadow depth pass cannot keep it. A cascade's shadow map has a texel world size of
    /// <c>2 * radius / resolution</c>, which grows with the cascade, and a noise cell SMALLER than a texel is not a
    /// dither any more: the depth pass then punches each surviving fragment into isolated texels, and there is no
    /// coherent shape left for the receiver's 3x3 kernel to resolve. So the shadow pass evaluates the same noise at
    /// a per-cascade scale that keeps the cell at least <see cref="MinCellTexels"/> texels across, capped at the
    /// base scale so near cascades (where the base cell is already many texels) are bit-identical to before.
    /// </para>
    /// </remarks>
    internal static class ShadowDissolveNoise
    {
        /// <summary>The base world-space noise scale (cell size = 1 / this = ~16.7 cm). The colour passes use this
        /// unconditionally; the shadow depth pass uses it as the cap in <see cref="ScaleForCascade"/>.</summary>
        public const float BaseScale = 6f;

        /// <summary>The GLSL spelling of <see cref="BaseScale"/>, concatenated into the shader sources so the number
        /// exists once. Pinned equal to <see cref="BaseScale"/> by a headless test (a const string cannot be derived
        /// from a float at compile time, so the pair is checked rather than computed).</summary>
        public const string BaseScaleGlsl = "6.0";

        /// <summary>How many shadow-map texels a noise cell must span for the dither to survive the depth pass and
        /// the receiver's 3x3 kernel. Four is the smallest value that leaves a cell wider than the kernel is, so a
        /// tap can land wholly inside a hole or wholly inside a surviving blob.</summary>
        public const float MinCellTexels = 4f;

        /// <summary>
        /// The noise scale the SHADOW depth pass should evaluate a dissolve at for a cascade fitted to
        /// <paramref name="cascadeRadius"/> at <paramref name="resolution"/> texels per axis:
        /// <c>min(BaseScale, 1 / (MinCellTexels * texelWorldSize))</c>. Never coarser than the colour pass (so a
        /// near cascade, where the base cell is already dozens of texels, gets exactly the base scale and the whole
        /// pre-#391 near-field behaviour is unchanged), and never finer than <see cref="MinCellTexels"/> texels.
        /// </summary>
        public static float ScaleForCascade(float cascadeRadius, int resolution)
        {
            float texel = ShadowMapMath.TexelWorldSize(cascadeRadius, resolution);
            float coarsest = 1f / MathF.Max(MinCellTexels * texel, 1e-6f);
            return MathF.Min(BaseScale, coarsest);
        }

        /// <summary>How many shadow-map texels one noise cell spans for a cascade fitted to
        /// <paramref name="cascadeRadius"/> at <paramref name="resolution"/>, at the scale
        /// <see cref="ScaleForCascade"/> picks. The contract <see cref="MinCellTexels"/> pins.</summary>
        public static float CellTexels(float cascadeRadius, int resolution)
        {
            float texel = ShadowMapMath.TexelWorldSize(cascadeRadius, resolution);
            return 1f / ScaleForCascade(cascadeRadius, resolution) / texel;
        }
    }
}
