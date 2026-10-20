using System;
using System.Globalization;
using System.Text;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// THE VISIBILITY DECISION'S FAST PATH, WRITTEN OUT (TAA shadow visibility plan, Task 7f, R2 and R4). D's
    /// <c>temporalVisibilityChange</c> reads 101 texels a pixel: per accepted neighbour of the 3x3 a bilinear history
    /// footprint of four corners, each read for its stored depth and its visibility. Its history position chain is
    /// componentwise, so a column's x and a row's y repeat across the 3x3, and where the three columns and the three
    /// rows land on consecutive texels the nine footprints are one 4x4 block. This writes
    /// <c>temporalVisibilityReleases</c>: D's three column and three row chains, computed once on the diagonal, then
    /// either D's function verbatim or the block read once, with every acceptance, weight and sum in D's order and
    /// D's emitted <c>Fma</c> pattern as explicit <c>fma()</c>.
    /// <para>The text is straight-line, written here with every block index fixed, so no compiler meets a loop, an
    /// array or a load placed behind a per-texel acceptance. Every program is a constant, so the text it writes is
    /// committed as <see cref="ShaderSources.TemporalVisibilityReleasesGlsl"/>, and a device-free test holds the two
    /// equal.</para>
    /// <para>R3-lite, the current mask: which of the 3x3 lie on the centre's surface, and whether the centre's own
    /// depth is finite, is <c>temporalVisibilityCurrentMask</c> over the same 3x3 in D's order and arithmetic
    /// (<see cref="ShaderSources.TemporalVisibilityMaskGlsl"/>). The split's first pass stores it per internal texel and
    /// the fused entry computes it inline, so the fast path reads no current depth or motion, only the nine current
    /// visibilities.</para>
    /// <para>R4, the range skip: the history mean is a non-negative weighting of the accepted block values, so it
    /// lies in the block's range, widened by the rounding of 36 fused terms and a division, well under
    /// <c>2^-13</c>. Where the current mean lies within <c>0.1 - 2^-13</c> of both ends, D's change cannot exceed
    /// 0.1 and its decision is false, so the stored depths are never read. The test is multiplied through by the
    /// count, at most nine, so its own rounding stays under <c>2^-19</c>, inside the margin, and the current mean keeps
    /// its one use, in D's change.</para>
    /// </summary>
    internal static class TemporalVisibilityFastPath
    {
        /// <summary>The texel fetches the fast path issues a pixel when it reads the whole block: nine current
        /// visibilities, sixteen previous visibilities and sixteen stored depths.</summary>
        internal const int Fetches = 9 + 16 + 16;

        /// <summary>
        /// THE BINADE RULE, as the generated text tests it per column (row) of the block: whether column
        /// <paramref name="at"/> under jitter <paramref name="jitter"/> on its axis sends the block to D's slow path.
        /// <para>Why. D's chain begins <c>(at + 0.5) - J</c>, and FXC at the shipped flags compiles it as written in
        /// some programs and reassociated, <c>(at - J) + 0.5</c>, in others (D's own helper and two of the fast path's
        /// six components in hosted run 39362804399). Take integer <c>at</c> with <c>0 &lt;= at &lt; 2^22</c> and float
        /// <c>J</c> in (-0.5, 0.5). <c>at + 0.5</c> is exact, so the written form rounds once, at
        /// <c>t + 0.5</c> where <c>t = at - J</c>. The reassociated form rounds <c>t</c>, then <c>fl(t) + 0.5</c>.
        /// Where <c>t</c> and <c>t + 0.5</c> lie in one closed binade <c>[2^e, 2^(e+1)]</c>, both are rounded to the
        /// same grid of spacing <c>u = 2^(e-23)</c>, ties to even, and 0.5 is an even multiple of <c>u</c> below
        /// <c>2^22</c>, so the rounding commutes with adding 0.5 and the two forms give equal bits. For
        /// <c>at = 0</c> both are <c>fl(0.5 - J)</c>, since <c>-J</c> is exact. For <c>at &gt;= 1</c> with
        /// <c>2^e &lt;= at &lt; 2^(e+1)</c>, the values span <c>(at - J, at + 0.5 - J)</c>. Negative jitter puts both in
        /// <c>(at, at + 1)</c>, inside the binade, as does positive jitter when <c>at &gt;= 2^e + 1</c>, since then
        /// <c>at - J &gt; 2^e + 0.5</c>. Only positive jitter at <c>at = 2^e</c> puts <c>t</c> below <c>2^e</c> and
        /// <c>t + 0.5</c> above it, where the two roundings land on different grids and can differ by an ulp. Zero and
        /// negative zero jitter give <c>t = at</c> exactly. So the forms are equal wherever this returns false.
        /// TemporalVisibilityBinadeTests checks it in float32 for every <c>at</c> to 4096.</para>
        /// <para>The jitter is tested by its bits, so a positive denormal or a positive NaN also takes D's path and no
        /// compiler rewrites an ordered comparison.</para>
        /// </summary>
        internal static bool SlowForBinade(int at, float jitter) =>
            at > 0 && (at & (at - 1)) == 0 && BitConverter.SingleToInt32Bits(jitter) > 0;

        /// <summary>The GLSL of <c>temporalVisibilityReleases</c> and its path bits, LF line endings.</summary>
        internal static string Generate() => Generate(static block => block);

        /// <summary>As <see cref="Generate()"/>, with each verbatim block of this file passed through
        /// <paramref name="checkout"/> first. A verbatim block carries this file's line endings as checked out, so a
        /// test replays a CRLF checkout through it. The text is LF whatever the blocks carry.</summary>
        internal static string Generate(Func<string, string> checkout)
        {
            var g = new StringBuilder();
            void Verbatim(string text) => g.Append(checkout(text));
            Verbatim(@"
// THE SHIPPED DECISION, generated by TemporalVisibilityFastPath. Edit the generator, never this text. The call site
// ignores the path bits, which tell the equivalence oracle which path ran and whether a change was computed.
const uint VisibilityFastPath = 1u, VisibilitySlowPath = 2u, VisibilityRangeSkip = 4u, VisibilityChangeComputed = 8u,
    VisibilitySlowBinade = 16u;
// Below this distance from both ends of the block's range no history mean can lie 0.1 from the current mean.
const float VisibilityRangeSkipMargin = VisibilityReleaseThreshold - 1.220703125e-4;

// A column or row that is a power of two, where (at + 0.5) - J and (at - J) + 0.5 can round apart under positive
// jitter. TemporalVisibilityFastPath.SlowForBinade states the proof.
bool temporalVisibilityPowerOfTwo(int at) { return at > 0 && (at & (at - 1)) == 0; }

// D's decision. Where the nine footprints form one 4x4 block inside the texture, the block is read once and every
// acceptance, weight and sum follows D's order and arithmetic, with the current acceptances from currentMask, the
// centre's temporalVisibilityCurrentMask. Anywhere else D's function runs as it shipped.
bool temporalVisibilityReleases(vec2 uv, ivec2 centre, ivec2 last, vec2 previousUv, float expectedDepth,
    uint currentMask, out float signedChange, out uint path) {
    signedChange = 0.0;
    // D's position chain is componentwise, so the diagonal neighbour (k, k) carries column k's x and row k's y.
");
            for (int k = 0; k < 3; k++)
            {
                string o = Offset(k - 1);
                g.Append($"    ivec2 at{k} = clamp(centre + ivec2({o}, {o}), ivec2(0), last);\n");
                g.Append($"    vec2 historyUv{k} = previousUv + (vec2(at{k}) + 0.5 - Jitter.xy) / Sizes.xy\n");
                g.Append("        - uv + PreviousJitter.xy / Sizes.xy;\n");
                g.Append($"    vec2 position{k} = fma(historyUv{k}, Sizes.xy, vec2(-0.5));\n");
                g.Append($"    ivec2 base{k} = ivec2(floor(position{k}));\n");
                g.Append($"    vec2 fraction{k} = position{k} - vec2(base{k});\n");
            }
            Verbatim(@"    // Consecutive bases on both axes, and the block inside the texture so that no corner clamps. An edge centre
    // fails the first test, because its clamped neighbour repeats the centre's chain.
    bool blockFits = base0 == base1 - 1 && base2 == base1 + 1 && all(greaterThanEqual(base1, ivec2(1)))
        && all(lessThanEqual(base1, last - 2));
    // FXC may compile these chains and D's in different association orders, which round apart only at a column or
    // row that is a power of two under positive jitter on its axis. Such a block takes D's path.
    bool binade = floatBitsToInt(Jitter.x) > 0 && (temporalVisibilityPowerOfTwo(at0.x)
            || temporalVisibilityPowerOfTwo(at1.x) || temporalVisibilityPowerOfTwo(at2.x))
        || floatBitsToInt(Jitter.y) > 0 && (temporalVisibilityPowerOfTwo(at0.y)
            || temporalVisibilityPowerOfTwo(at1.y) || temporalVisibilityPowerOfTwo(at2.y));
    if (!blockFits || binade) {
        path = VisibilitySlowPath | (blockFits ? VisibilitySlowBinade : 0u);
        bool valid = temporalVisibilityChange(uv, centre, last, previousUv, expectedDepth, signedChange);
        if (valid) path |= VisibilityChangeComputed;
        return valid && abs(signedChange) > VisibilityReleaseThreshold;
    }
    path = VisibilityFastPath;

    // The nine current visibilities and the block's previous visibility, read before any decision.
");
            for (int n = 0; n < 9; n++)
                g.Append($"    float current{n} = texelFetch(sampler2D(KeyShadowVisibility, LinearClamp), {Texel(n)}, 0).r;\n");
            g.Append("    ivec2 block = base1 - 1;\n");
            for (int b = 0; b < 16; b++)
                g.Append($"    float previous{Block(b)} = texelFetch(sampler2D(PreviousKeyShadowVisibility, LinearClamp), "
                    + $"block + ivec2({b % 4}, {b / 4}), 0).r;\n");
            Verbatim(@"    if ((currentMask & VisibilityCentreFinite) == 0u || !temporalVisibilityFiniteDepth(expectedDepth)) return false;
    float previousTolerance = VisibilityDepthTolerance * max(expectedDepth, 1.0e-5);

    // D's current rejection of each texel, from the mask, then its sum and count in D's order.
");
            for (int n = 0; n < 9; n++)
                g.Append($"    bool accepted{n} = (currentMask & {Bit(n)}u) != 0u;\n");
            g.Append("    float currentSum = 0.0;\n    float currentCount = 0.0;\n");
            for (int n = 0; n < 9; n++)
                g.Append($"    if (accepted{n}) {{ currentSum += current{n}; currentCount += 1.0; }}\n");
            Verbatim(@"    if (!(currentCount > 0.0)) return false;

    // R4: the history mean lies in the block's range, so a current mean this near both ends cannot release. The
    // test is taken on the sum and the count, the mean's distances times the count: a second use of the current
    // mean changed how Metal compiles D's own change below by an ulp.
");
            Reduce(g, "lowest", "min");
            Reduce(g, "highest", "max");
            Verbatim(@"    if (max(currentSum - lowest * currentCount, highest * currentCount - currentSum)
        <= VisibilityRangeSkipMargin * currentCount) {
        path |= VisibilityRangeSkip;
        return false;
    }

    // D's history rejection, once a block texel, then D's 36 bilinear terms in D's order.
");
            for (int b = 0; b < 16; b++)
                g.Append($"    float stored{Block(b)} = texelFetch(sampler2D(PrevDepth, LinearClamp), "
                    + $"block + ivec2({b % 4}, {b / 4}), 0).r;\n");
            for (int b = 0; b < 16; b++)
                g.Append($"    bool held{Block(b)} = temporalVisibilityFiniteDepth(stored{Block(b)}) "
                    + $"&& abs(stored{Block(b)} - expectedDepth) <= previousTolerance;\n");
            for (int k = 0; k < 3; k++)
                g.Append($"    float columnNear{k} = 1.0 - fraction{k}.x;\n    float rowNear{k} = 1.0 - fraction{k}.y;\n");
            g.Append("    float previousSum = 0.0;\n    float previousWeight = 0.0;\n");
            for (int n = 0; n < 9; n++)
            {
                int column = n % 3, row = n / 3;
                g.Append($"    if (accepted{n}) {{\n");
                for (int j = 0; j < 2; j++)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        string corner = Block((row + j) * 4 + column + i);
                        string wx = i == 0 ? $"columnNear{column}" : $"fraction{column}.x";
                        string wy = j == 0 ? $"rowNear{row}" : $"fraction{row}.y";
                        g.Append($"        if (held{corner}) {{\n");
                        g.Append($"            previousSum = fma(previous{corner}, {wx} * {wy}, previousSum);\n");
                        g.Append($"            previousWeight = fma({wx}, {wy}, previousWeight);\n");
                        g.Append("        }\n");
                    }
                }
                g.Append("    }\n");
            }
            Verbatim(@"    if (!(previousWeight > 0.0)) return false;
    signedChange = currentSum / currentCount - previousSum / previousWeight;
    path |= VisibilityChangeComputed;
    return abs(signedChange) > VisibilityReleaseThreshold;
}
");
            return g.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        static string Offset(int offset) => offset.ToString(CultureInfo.InvariantCulture);

        // Step n's bit of the current mask, temporalVisibilityCurrentBit's 1u << n.
        static string Bit(int n) => (1u << n).ToString(CultureInfo.InvariantCulture);

        // Neighbour n of the 3x3 in D's order, rows first: column n % 3 takes chain (n % 3)'s x, row n / 3 chain
        // (n / 3)'s y.
        static string Texel(int n) => $"ivec2(at{n % 3}.x, at{n / 3}.y)";

        // Block texel b, column b % 4 and row b / 4 from the block's corner, as a two-digit name.
        static string Block(int b) => $"{b % 4}{b / 4}";

        // The block's minimum or maximum, row by row and then across the rows. Either is exact in any order.
        static void Reduce(StringBuilder g, string name, string op)
        {
            for (int row = 0; row < 4; row++)
                g.Append($"    float {name}{row} = {op}({op}(previous{Block(row * 4)}, previous{Block(row * 4 + 1)}), "
                    + $"{op}(previous{Block(row * 4 + 2)}, previous{Block(row * 4 + 3)}));\n");
            g.Append($"    float {name} = {op}({op}({name}0, {name}1), {op}({name}2, {name}3));\n");
        }
    }
}
