using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>One cascade filter lookup, and optionally its hand-off blend toward the next cascade.</summary>
    /// <param name="Name">What the case exercises, printed with any mismatch.</param>
    /// <param name="Cascade">The selected cascade, the atlas column the lookup clamps inside.</param>
    /// <param name="Uv">Cascade-local UV of the lookup.</param>
    /// <param name="Depth">The already biased receiver depth.</param>
    /// <param name="Blend">Whether the case also filters <c>Cascade + 1</c> and mixes the two like <c>sampleKeyShadow</c>.</param>
    /// <param name="NextUv">Cascade-local UV of the lookup in the next cascade.</param>
    /// <param name="NextDepth">The receiver depth in the next cascade.</param>
    /// <param name="Weight">The selected cascade's share, the smoothstep <c>sampleKeyShadow</c> mixes with.</param>
    internal readonly record struct ShadowFilterCase(string Name, int Cascade, Vector2 Uv, float Depth,
        bool Blend = false, Vector2 NextUv = default, float NextDepth = 0f, float Weight = 1f);

    /// <summary>
    /// The CPU reference for the cascade comparison filter, its atlas and its case set. The reference is the spec's
    /// definition written out longhand: nine bilinear comparison taps at offsets <c>{-1,0,1}</c> squared, each
    /// interpolating four point comparisons, which share a 4x4 footprint weighted <c>[1-f, 1, 1, f]</c> per axis and
    /// divided by nine. Every sampled texel centre clamps inside its own cascade column. Stored depths are never
    /// interpolated, only the 0 or 1 comparison results.
    /// <para>
    /// The atlas is four 8x8 cascades side by side. Each column's edge texels disagree with the neighbouring column's
    /// facing edge, so a tap that crossed a column instead of clamping changes the answer. Coordinates and stored depths
    /// are dyadic and every comparison is exact, so the GPU and CPU agree to rounding of the final division.
    /// </para>
    /// </summary>
    internal static class ShadowFilterOracle
    {
        internal const int Resolution = 8;
        internal const int Count = 4;
        internal const int AtlasWidth = Resolution * Count;
        internal const float TexelStep = 1f / Resolution;

        internal const float Occluder = 0.25f;
        internal const float Mid = 0.5f;
        internal const float High = 0.75f;
        internal const float Clear = 1f;

        /// <summary>The R32F atlas, row-major from the row at V = 0.</summary>
        internal static float[] Atlas()
        {
            float[] palette = { Occluder, Occluder, Mid, Clear, High };
            var atlas = new float[AtlasWidth * Resolution];
            for (int c = 0; c < Count; c++)
                for (int j = 0; j < Resolution; j++)
                    for (int i = 0; i < Resolution; i++)
                    {
                        float d = palette[(i * 7 + j * 13 + c * 5) % palette.Length];
                        // Both edges of an even column are occluders and both edges of an odd one are clear, so every
                        // pair of facing edges across a column boundary disagrees for any receiver depth below 1.
                        if (i == 0 || i == Resolution - 1) d = c % 2 == 0 ? Occluder : Clear;
                        atlas[j * AtlasWidth + c * Resolution + i] = d;
                    }
            return atlas;
        }

        /// <summary>The shared per-axis weights of the four footprint texels for texel fraction <paramref name="f"/>.</summary>
        internal static float[] Weights(float f) => new[] { 1f - f, 1f, 1f, f };

        /// <summary>The bilinear 3x3 comparison filter, longhand.</summary>
        internal static float Filter(float[] atlas, int cascade, Vector2 uv, float depth)
        {
            float tx = uv.X / TexelStep - 0.5f, ty = uv.Y / TexelStep - 0.5f;
            float bx = MathF.Floor(tx), by = MathF.Floor(ty);
            float[] wx = Weights(tx - bx), wy = Weights(ty - by);
            float lit = 0f;
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                {
                    int ix = Math.Clamp((int)bx - 1 + x, 0, Resolution - 1);
                    int iy = Math.Clamp((int)by - 1 + y, 0, Resolution - 1);
                    float d = atlas[iy * AtlasWidth + cascade * Resolution + ix];
                    lit += (depth <= d ? 1f : 0f) * wx[x] * wy[y];
                }
            return lit / 9f;
        }

        /// <summary>The filter this one replaces: nine point comparisons on the whole-texel grid around the lookup.</summary>
        internal static float PointFilter(float[] atlas, int cascade, Vector2 uv, float depth)
        {
            float half = TexelStep * 0.5f;
            float lit = 0f;
            for (int oy = -1; oy <= 1; oy++)
                for (int ox = -1; ox <= 1; ox++)
                {
                    float u = Math.Clamp(uv.X + ox * TexelStep, half, 1f - half);
                    float v = Math.Clamp(uv.Y + oy * TexelStep, half, 1f - half);
                    int ix = Math.Min((int)MathF.Floor(u * Resolution), Resolution - 1);
                    int iy = Math.Min((int)MathF.Floor(v * Resolution), Resolution - 1);
                    lit += depth <= atlas[iy * AtlasWidth + cascade * Resolution + ix] ? 1f : 0f;
                }
            return lit / 9f;
        }

        /// <summary>A case's expected result, the hand-off mix included.</summary>
        internal static float Expected(float[] atlas, ShadowFilterCase c)
        {
            float lit = Filter(atlas, c.Cascade, c.Uv, c.Depth);
            if (!c.Blend) return lit;
            float next = Filter(atlas, c.Cascade + 1, c.NextUv, c.NextDepth);
            return next + (lit - next) * c.Weight;
        }

        /// <summary>Cascade-local UV of texel coordinate <paramref name="t"/>, where integer <c>t</c> is a texel centre.</summary>
        internal static float UvOf(float t) => (t + 0.5f) / Resolution;

        /// <summary>
        /// Every case: interior lookups on, before and after texel centres, every border of every column in both axes
        /// (including lookups past the last texel centre, which clamp), and the hand-off blend between each adjacent
        /// pair. Receiver depths cover occluded, lit and exactly equal stored depths.
        /// </summary>
        internal static IReadOnlyList<ShadowFilterCase> Cases()
        {
            float[] depths = { 0.4f, Mid, High, 0.9f, Occluder };
            var cases = new List<ShadowFilterCase>();
            int n = 0;
            float NextDepth() => depths[n++ % depths.Length];

            (float X, float Y, string What)[] interior =
            {
                (3f, 4f, "on a centre"),
                (3.25f, 4f, "+0.25 texel in x"),
                (2.75f, 4f, "-0.25 texel in x"),
                (3f, 4.25f, "+0.25 texel in y"),
                (3f, 3.75f, "-0.25 texel in y"),
                (3.5f, 2.5f, "half texel in both"),
                (4.75f, 2.25f, "+0.75 x, +0.25 y"),
                (2.25f, 5.75f, "+0.25 x, +0.75 y"),
                (0f, 0f, "on the first corner centre"),
                (7f, 7f, "on the last corner centre"),
                (-0.25f, -0.5f, "past the first corner"),
                (7.25f, 7.5f, "past the last corner"),
            };
            float[] border = { -0.5f, -0.25f, 0f, 0.25f, 6.75f, 7f, 7.25f, 7.5f };
            for (int c = 0; c < Count; c++)
            {
                foreach ((float x, float y, string what) in interior)
                    cases.Add(Case(c, x, y, NextDepth(), $"cascade {c} {what}"));
                foreach (float t in border)
                {
                    cases.Add(Case(c, t, 3.5f, NextDepth(), $"cascade {c} x border at texel {Format(t)}"));
                    cases.Add(Case(c, 3.5f, t, NextDepth(), $"cascade {c} y border at texel {Format(t)}"));
                }
            }

            foreach (float depth in new[] { Occluder, Mid, High, Clear })
                cases.Add(Case(1, 3.25f, 4.5f, depth, $"cascade 1 receiver equal to stored depth {Format(depth)}"));

            for (int c = 0; c < Count - 1; c++)
                foreach (float w in new[] { 0.25f, 0.5f })
                    cases.Add(new ShadowFilterCase($"blend cascade {c} toward {c + 1} at weight {Format(w)}", c,
                        new Vector2(UvOf(6.75f), UvOf(3.25f)), 0.4f, Blend: true,
                        NextUv: new Vector2(UvOf(1.25f), UvOf(4.5f)), NextDepth: Mid, Weight: w));
            return cases;
        }

        static ShadowFilterCase Case(int cascade, float x, float y, float depth, string name)
            => new(name, cascade, new Vector2(UvOf(x), UvOf(y)), depth);

        static string Format(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
