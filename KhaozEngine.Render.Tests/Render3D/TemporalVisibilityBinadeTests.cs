using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// THE BINADE RULE, PROVED IN FLOAT32 (TAA shadow visibility plan, Task 7f, fix round 3). The history position chain
    /// starts <c>(at + 0.5) - J</c>, and FXC compiles it as written in some programs and as <c>(at - J) + 0.5</c> in
    /// others. The fast path sends a block to D's slow path wherever any of its columns (rows) is a power of two and the
    /// jitter on that axis is positive (<see cref="TemporalVisibilityFastPath.SlowForBinade"/>). This replays both
    /// association orders in System.Single, every step rounded to float, for every <c>at</c> from 0 to 16384, the
    /// largest internal size, against a dense set of jitters in (-0.5, 0.5): both signs, zero, denormals, the ends of
    /// every binade down to 2^-40, the last few thousand floats below 0.5, an even sweep and, since review 7f r3 (m-5),
    /// every offset <see cref="TemporalJitter.Offset"/> returns on either axis for every phase count up to
    /// <see cref="TemporalJitter.MaxPhaseCount"/>, the only jitter the shader is given. Wherever the rule lets the fast
    /// path run, the two orders must give the same bits. Where it does not, they must differ somewhere, so the rule is
    /// not vacuous. Device-free.
    /// </summary>
    public sealed class TemporalVisibilityBinadeTests(ITestOutputHelper output)
    {
        const int LastColumn = 16384;

        [Fact]
        public void BothAssociationOrdersAgreeWhereverTheRuleKeepsTheFastPath()
        {
            float[] jitters = Jitters();
            var real = new HashSet<uint>(RealOffsets().Select(Bits));
            long agreed = 0, slowPairs = 0, slowDiffer = 0, realPairs = 0, realSlowDiffer = 0;
            var counterexamples = new List<string>();
            var differByColumn = new SortedDictionary<int, long>();
            for (int at = 0; at <= LastColumn; at++)
            {
                float column = at;
                foreach (float jitter in jitters)
                {
                    uint written = Bits(Written(column, jitter)), reassociated = Bits(Reassociated(column, jitter));
                    bool isReal = real.Contains(Bits(jitter));
                    if (isReal) realPairs++;
                    if (TemporalVisibilityFastPath.SlowForBinade(at, jitter))
                    {
                        slowPairs++;
                        if (written == reassociated) continue;
                        slowDiffer++;
                        if (isReal) realSlowDiffer++;
                        differByColumn[at] = differByColumn.GetValueOrDefault(at) + 1;
                    }
                    else if (written == reassociated) agreed++;
                    else if (counterexamples.Count < 16)
                        counterexamples.Add($"at {at}, J {jitter:R} (0x{Bits(jitter):x8}): (at + 0.5) - J is 0x{written:x8}, "
                            + $"(at - J) + 0.5 is 0x{reassociated:x8}");
                }
            }
            output.WriteLine($"{LastColumn + 1} columns x {jitters.Length} jitters: {agreed} fast-path pairs agree, "
                + $"{slowPairs} pairs take the slow path, {slowDiffer} of them differ");
            output.WriteLine($"  {real.Count} distinct real jitter offsets: {realPairs} pairs, {realSlowDiffer} slow pairs "
                + "differ");
            foreach ((int at, long count) in differByColumn) output.WriteLine($"  at {at}: {count} pairs differ");
            Assert.True(counterexamples.Count == 0,
                "the two orders differ where the fast path runs:\n  " + string.Join("\n  ", counterexamples));
            Assert.True(slowDiffer > 0, "no pair the rule sends to the slow path differs, so the rule is vacuous");
            Assert.Equal((long)real.Count * (LastColumn + 1), realPairs);
            // The real offsets alone meet the binade, all 201 pairs at a power of two under positive jitter, as review
            // 7f r3 found replaying them independently.
            Assert.Equal(201, realSlowDiffer);
            // Every column that differs is a power of two, the only columns the rule names.
            Assert.All(differByColumn.Keys, at => Assert.True(at > 0 && (at & (at - 1)) == 0, $"at {at}"));
        }

        /// <summary>The rule itself: a positive power of two with a jitter whose bits read positive, so zero and
        /// negative zero keep the fast path and a positive denormal does not.</summary>
        [Theory]
        [InlineData(1, 0.25f, true)]
        [InlineData(2048, 1e-30f, true)]
        [InlineData(2048, -0.25f, false)]
        [InlineData(2048, 0f, false)]
        [InlineData(0, 0.25f, false)]
        [InlineData(3, 0.25f, false)]
        [InlineData(1023, 0.25f, false)]
        [InlineData(1025, 0.25f, false)]
        [InlineData(4096, 1.401298E-45f, true)]
        public void TheRuleNamesPositivePowersOfTwoWithPositiveJitter(int at, float jitter, bool slow)
            => Assert.Equal(slow, TemporalVisibilityFastPath.SlowForBinade(at, jitter));

        [Fact]
        public void NegativeZeroJitterKeepsTheFastPath()
            => Assert.False(TemporalVisibilityFastPath.SlowForBinade(2048, BitConverter.UInt32BitsToSingle(0x80000000u)));

        // The chain as the source writes it, one rounding at the subtraction, since at + 0.5 is exact below 2^23.
        static float Written(float at, float jitter)
        {
            float half = (float)(at + 0.5f);
            return (float)(half - jitter);
        }

        // The chain as FXC reassociated it in D, two roundings.
        static float Reassociated(float at, float jitter)
        {
            float shifted = (float)(at - jitter);
            return (float)(shifted + 0.5f);
        }

        static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

        /// <summary>Every offset the sequence gives, both axes, at every phase count up to the longest, all inside
        /// (-0.5, 0.5). The first frame's x is zero.</summary>
        [Fact]
        public void TheRealOffsetsLieInsideTheProvedRange()
        {
            float[] offsets = RealOffsets();
            Assert.Equal(2 * TemporalJitter.MaxPhaseCount, offsets.Length);
            Assert.All(offsets, j => Assert.True(j > -0.5f && j < 0.5f, $"J {j:R}"));
            Assert.Contains(0f, offsets);
            Assert.Equal(-0.49609375f, offsets.Min());
            Assert.Equal(0.4921875f, offsets.Max());
        }

        // TemporalJitter.Offset on both axes for every frame of every phase count up to MaxPhaseCount. A longer
        // sequence only extends a shorter one, so this is Halton indices 1 to MaxPhaseCount, each axis kept apart.
        static float[] RealOffsets()
        {
            var set = new HashSet<(int Axis, uint Bits)>();
            for (int phases = 1; phases <= TemporalJitter.MaxPhaseCount; phases++)
                for (long frame = 0; frame < phases; frame++)
                {
                    Vector2 offset = TemporalJitter.Offset(frame, phases);
                    set.Add((0, Bits(offset.X)));
                    set.Add((1, Bits(offset.Y)));
                }
            return set.Select(o => BitConverter.UInt32BitsToSingle(o.Bits)).ToArray();
        }

        static float[] Jitters()
        {
            var set = new HashSet<uint>(RealOffsets().Select(Bits));
            void Add(float j)
            {
                if (j > -0.5f && j < 0.5f) { set.Add(Bits(j)); set.Add(Bits(-j)); }
            }
            Add(0f);
            for (int k = 1; k <= 64; k++) Add(BitConverter.UInt32BitsToSingle((uint)k));   // denormals
            // Both ends and an even spread of every binade from 2^-40 up to [0.25, 0.5).
            for (int e = -40; e <= -2; e++)
            {
                float low = MathF.ScaleB(1f, e), next = MathF.ScaleB(1f, e + 1);
                for (int k = 0; k < 64; k++)
                {
                    Add(BitConverter.UInt32BitsToSingle(Bits(low) + (uint)k));
                    Add(BitConverter.UInt32BitsToSingle(Bits(next) - 1 - (uint)k));
                }
                for (int k = 1; k < 256; k++) Add(low + (next - low) * (k / 256f));
            }
            // The last floats below 0.5, where (at - J) + 0.5 sits closest to the next column.
            for (int k = 1; k <= 4096; k++) Add(BitConverter.UInt32BitsToSingle(Bits(0.5f) - (uint)k));
            // An even sweep of (-0.5, 0.5) and a seeded random set.
            for (int k = 1; k < 8192; k++) Add(k / 16384f);
            var random = new Random(7919);
            for (int k = 0; k < 8192; k++) Add((float)random.NextDouble() * 0.5f);
            return set.Select(BitConverter.UInt32BitsToSingle).OrderBy(j => j).ToArray();
        }
    }
}
