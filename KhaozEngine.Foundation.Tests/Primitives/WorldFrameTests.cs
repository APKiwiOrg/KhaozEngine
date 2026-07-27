using System;
using System.Numerics;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives
{
    /// <summary>
    /// The floating-origin frame invariants. These are the exactness claims the whole design rests on, so every
    /// assertion here compares RAW BITS rather than an epsilon: "close enough" is what a float epsilon proves, and
    /// what the design needs is that a re-anchor introduces literally no error, which is a stronger and checkable
    /// statement.
    /// </summary>
    public class WorldFrameTests
    {
        static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

        // A spread of locals inside one grid cell, including the exact half-grid edges, a denormal-adjacent tiny
        // value, and values that are NOT representable as a short multiple of anything (so the sum has to be checked
        // rather than assumed).
        static readonly float[] SweepLocals =
        {
            0f, 1f, -1f, 0.5f, -0.5f, 63.9999f, -63.9999f, 64f, -64f, 96.0001f, -96.0001f,
            127.99999f, -127.99999f, 1e-7f, -1e-7f, 12.3456789f, -87.6543f, 33.333332f,
        };

        [Fact]
        public void Origin_is_default_and_its_anchor_is_exactly_zero()
        {
            Assert.Equal(default, WorldFrame.Origin);
            Assert.Equal(Vector3.Zero, WorldFrame.Origin.Anchor);
            // Bitwise, not just ==: a -0.0 anchor would compare equal yet is a different value.
            Assert.Equal(0u, Bits(WorldFrame.Origin.Anchor.X));
            Assert.Equal(0u, Bits(WorldFrame.Origin.Anchor.Y));
            Assert.Equal(0u, Bits(WorldFrame.Origin.Anchor.Z));
        }

        [Fact]
        public void At_the_origin_the_whole_api_is_byte_identical_to_unframed_math()
        {
            WorldFrame f = WorldFrame.Origin;
            foreach (float a in SweepLocals)
                foreach (float b in SweepLocals)
                {
                    var p = new Vector3(a, 12.5f, b);
                    Vector3 local = f.ToLocal(p);
                    Assert.Equal(Bits(p.X), Bits(local.X));
                    Assert.Equal(Bits(p.Y), Bits(local.Y));
                    Assert.Equal(Bits(p.Z), Bits(local.Z));
                    Vector3 back = f.ToWorld(p);
                    Assert.Equal(Bits(p.X), Bits(back.X));
                    Assert.Equal(Bits(p.Z), Bits(back.Z));
                }
        }

        [Fact]
        public void Anchors_are_exactly_representable_across_the_whole_short_range()
        {
            for (int i = short.MinValue; i <= short.MaxValue; i++)
            {
                var f = new WorldFrame((short)i, (short)i);
                Vector3 a = f.Anchor;
                // The anchor must be the exact integer multiple: recovering the index by division must land back on
                // i with no rounding, which is only true while the product is exact.
                Assert.Equal(i, (int)(a.X / WorldFrame.Grid));
                Assert.Equal(i, (int)(a.Z / WorldFrame.Grid));
                Assert.Equal(0f, a.Y);
                Assert.Equal(i * 128f, a.X);
            }
        }

        [Fact]
        public void Nearest_rounds_rather_than_floors_so_a_fresh_local_lands_inside_half_a_grid()
        {
            // Round: a coordinate 0.4 of a grid past an anchor stays on that anchor; 0.6 past it moves to the next.
            Assert.Equal(new WorldFrame(0, 0), WorldFrame.Nearest(new Vector3(50f, 0f, -50f)));
            Assert.Equal(new WorldFrame(1, -1), WorldFrame.Nearest(new Vector3(80f, 0f, -80f)));
            Assert.Equal(new WorldFrame(781, 0), WorldFrame.Nearest(new Vector3(100_000f, 0f, 0f)));

            // Property: for a swept set of world coordinates, the local after anchoring never exceeds half a grid.
            for (float w = -5000f; w <= 5000f; w += 3.7f)
            {
                WorldFrame f = WorldFrame.Nearest(w, -w);
                Vector2 local = f.ToLocalXz(w, -w);
                Assert.True(MathF.Abs(local.X) <= WorldFrame.Grid * 0.5f + 1e-3f,
                    $"local X {local.X} at world {w} exceeded half a grid (Nearest floored instead of rounding?)");
                Assert.True(MathF.Abs(local.Y) <= WorldFrame.Grid * 0.5f + 1e-3f);
            }
        }

        [Fact]
        public void Nearest_saturates_at_the_short_range_rather_than_wrapping()
        {
            Assert.Equal(short.MaxValue, WorldFrame.Nearest(1e12f, 0f).X);
            Assert.Equal(short.MinValue, WorldFrame.Nearest(-1e12f, 0f).X);
            Assert.Equal(short.MaxValue, WorldFrame.Nearest(0f, float.MaxValue).Z);
        }

        [Fact]
        public void Reanchor_is_bit_exact()
        {
            // Invariant 1, scoped to the RE-ANCHOR path: the target is drawn from Nearest() on a local that has
            // passed the ReanchorRadius trigger, which is exactly what guarantees the magnitude shrinks and the
            // lemma's precondition holds. Sweeping arbitrary frame pairs here would assert something false.
            foreach (WorldFrame source in new[] { WorldFrame.Origin, new WorldFrame(781, -1200), new WorldFrame(-3, 7) })
                foreach (float dx in new[] { 96.5f, -96.5f, 120f, -120f, 127.9f, -127.9f, 100f, -100f })
                    foreach (float dz in new[] { 96.5f, -96.5f, 127.9f, -127.9f })
                    {
                        var local = new Vector3(dx, 4.25f, dz);
                        Assert.True(WorldFrame.ShouldReanchor(local), "sweep value must actually trigger a re-anchor");

                        Vector3 world = source.ToWorld(local);
                        WorldFrame target = WorldFrame.Nearest(world);
                        Vector3 moved = local + source.DeltaTo(target);

                        // The re-anchored local must reproduce the SAME world position, bit for bit. This is the
                        // lemma: translating a local by an exact grid multiple that does not grow its magnitude
                        // introduces no error at all.
                        Vector3 reWorld = target.ToWorld(moved);
                        Assert.Equal(Bits(world.X), Bits(reWorld.X));
                        Assert.Equal(Bits(world.Z), Bits(reWorld.Z));

                        // Re-DERIVING the local from the absolute position is strictly worse, and that asymmetry is
                        // the reason a re-anchor translates rather than re-derives. The absolute has already been
                        // rounded to the float32 lattice of ITS magnitude, so ToLocal cannot recover what the
                        // translation preserved: the two agree only to that lattice step, not bit for bit.
                        Vector3 direct = target.ToLocal(world);
                        float worldUlp = MathF.Max(MathF.Abs(world.X), 1f) * 1.2e-7f;
                        Assert.True(MathF.Abs(direct.X - moved.X) <= worldUlp,
                            $"re-derived local {direct.X} strayed from the translated {moved.X} by more than one ULP of {world.X}");
                        // Y is never framed.
                        Assert.Equal(Bits(local.Y), Bits(moved.Y));
                    }
        }

        [Fact]
        public void Round_to_nearest_never_grows_a_locals_magnitude_on_the_reanchor_path()
        {
            // Invariant 2: the lemma's precondition, asserted directly, so a future change to floor anchoring fails
            // a test instead of silently rounding. Also scoped to the re-anchor path: it is the property Nearest
            // plus the "> 96" trigger produce together, not a property of frame conversion in general.
            foreach (WorldFrame source in new[] { WorldFrame.Origin, new WorldFrame(781, -1200), new WorldFrame(12, 12) })
                for (float dx = -127.5f; dx <= 127.5f; dx += 0.9f)
                    for (float dz = -127.5f; dz <= 127.5f; dz += 11.3f)
                    {
                        var local = new Vector3(dx, 0f, dz);
                        if (!WorldFrame.ShouldReanchor(local)) continue;
                        WorldFrame target = WorldFrame.Nearest(source.ToWorld(local));
                        Vector3 moved = local + source.DeltaTo(target);
                        Assert.True(MathF.Abs(moved.X) <= MathF.Abs(local.X) + 1e-6f,
                            $"|X| grew from {local.X} to {moved.X}: the lemma's precondition is violated");
                        Assert.True(MathF.Abs(moved.Z) <= MathF.Abs(local.Z) + 1e-6f,
                            $"|Z| grew from {local.Z} to {moved.Z}: the lemma's precondition is violated");
                    }
        }

        [Fact]
        public void An_arbitrary_frame_conversion_is_exact_to_half_a_ulp_not_bit_exact()
        {
            // The path the two invariants above deliberately exclude. A conversion between two frames chosen
            // independently (adjacent 60 m shard cells, say) CAN grow the local's magnitude across a binade, so it
            // is exact only to half a ULP of the destination. Asserting bit-identity here is the mistake this test
            // exists to prevent; asserting nothing is how a real regression would reach a shard server unnoticed.
            const float bound = 1f / 262144f;   // 2^-18 m, about 3.8 micrometres
            const float cellSize = 60f;
            float worst = 0f;
            for (int cell = -40; cell < 40; cell++)
            {
                WorldFrame a = WorldFrame.Nearest((cell + 0.5f) * cellSize, 0f);
                WorldFrame b = WorldFrame.Nearest((cell + 1.5f) * cellSize, 0f);
                for (float t = 0f; t < 1f; t += 0.017f)
                {
                    float world = (cell + 1f) * cellSize + t;
                    Vector3 localA = a.ToLocal(new Vector3(world, 0f, 0f));
                    Vector3 localB = localA + a.DeltaTo(b);
                    float roundTrip = b.ToWorld(localB).X;
                    worst = MathF.Max(worst, MathF.Abs(roundTrip - world));
                }
            }
            Assert.True(worst <= bound, $"handoff conversion drifted {worst} m, past the 2^-18 m bound");
        }

        [Fact]
        public void Hysteresis_gives_at_least_64_m_of_separation()
        {
            // A reversal: after a re-anchor at local 96.1 the new local is -31.9, so the entity must travel 64 m
            // back the way it came before it can trigger again. Walk it back and count the re-anchors.
            WorldFrame frame = WorldFrame.Origin;
            var pos = new Vector3(96.1f, 0f, 0f);
            int reanchors = 0;
            if (WorldFrame.ShouldReanchor(pos))
            {
                WorldFrame next = WorldFrame.Nearest(frame.ToWorld(pos));
                pos += frame.DeltaTo(next);
                frame = next;
                reanchors++;
            }
            Assert.Equal(1, reanchors);
            Assert.True(MathF.Abs(pos.X + 31.9f) < 1e-3f, $"expected the new local near -31.9, got {pos.X}");

            // Oscillate across the old boundary: 63 m of reversal is not enough to trigger again.
            float travelled = 0f;
            while (travelled < 63f)
            {
                pos.X -= 1f;
                travelled += 1f;
                Assert.False(WorldFrame.ShouldReanchor(pos),
                    $"re-anchored again after only {travelled} m of reversal (need 64)");
            }

            // A straight-line traversal re-anchors at most once per 128 m.
            frame = WorldFrame.Origin;
            pos = Vector3.Zero;
            reanchors = 0;
            for (int step = 0; step < 1000; step++)
            {
                pos.X += 1f;
                if (!WorldFrame.ShouldReanchor(pos)) continue;
                WorldFrame next = WorldFrame.Nearest(frame.ToWorld(pos));
                pos += frame.DeltaTo(next);
                frame = next;
                reanchors++;
            }
            Assert.True(reanchors <= 1000 / 128 + 1, $"{reanchors} re-anchors over 1000 m of straight-line travel");
        }

        [Fact]
        public void MaxLocalRadius_is_the_top_of_the_last_binade_that_fits_the_divergence_budget()
        {
            // Derived from the constants rather than trusted from a comment, so a future budget change cannot leave
            // the ceiling stale. Predicted 20 s divergence at a magnitude is 215 * ULP(magnitude); the ceiling is the
            // top of the last binade whose prediction fits 10 mm.
            static float Ulp(float magnitude)
            {
                int exp = (int)MathF.Floor(MathF.Log2(magnitude));
                return MathF.Pow(2f, exp - 23);
            }
            static float Predicted(float magnitude) => WorldFrame.Divergence20sUlps * Ulp(magnitude);

            // Just inside the ceiling fits the budget.
            Assert.True(Predicted(WorldFrame.MaxLocalRadius - 1f) <= WorldFrame.DivergenceBudgetMetres,
                "the binade below MaxLocalRadius should fit the budget");
            // The next binade up does not.
            Assert.True(Predicted(WorldFrame.MaxLocalRadius + 1f) > WorldFrame.DivergenceBudgetMetres,
                "the binade above MaxLocalRadius should exceed the budget: the ceiling has drifted");

            // And the design target (a 96 m per-axis trigger, so a 136 m planar worst case) sits four binades below
            // it with the stated 3x margin.
            float designTarget = MathF.Sqrt(2f) * WorldFrame.ReanchorRadius;
            Assert.True(Predicted(designTarget) * 3f <= WorldFrame.DivergenceBudgetMetres,
                $"the design target {designTarget} m predicts {Predicted(designTarget)} m, less than a 3x margin");
        }

        [Fact]
        public void ShouldReanchor_ignores_y_and_triggers_past_the_radius_on_either_planar_axis()
        {
            Assert.False(WorldFrame.ShouldReanchor(new Vector3(96f, 100_000f, -96f)));
            Assert.True(WorldFrame.ShouldReanchor(new Vector3(96.001f, 0f, 0f)));
            Assert.True(WorldFrame.ShouldReanchor(new Vector3(0f, 0f, -96.001f)));
        }

        [Fact]
        public void DeltaTo_is_the_translation_that_carries_a_local_into_the_target_frame()
        {
            var a = new WorldFrame(3, -5);
            var b = new WorldFrame(-2, 9);
            var local = new Vector3(10f, 3f, -20f);
            Vector3 world = a.ToWorld(local);
            Assert.Equal(b.ToLocal(world).X, (local + a.DeltaTo(b)).X);
            Assert.Equal(b.ToLocal(world).Z, (local + a.DeltaTo(b)).Z);
            // Y is never framed, so the delta has none.
            Assert.Equal(0f, a.DeltaTo(b).Y);
            Assert.Equal(Vector3.Zero, a.DeltaTo(a));
        }
    }
}
