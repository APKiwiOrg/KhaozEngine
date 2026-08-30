using KhaozEngine.Primitives;
using KhaozEngine.Telegraphs;
using Xunit;

namespace KhaozEngine.Tests.Telegraphs
{
    public class TelegraphResolveTests
    {
        [Fact]
        public void Progress_is_clamped_to_unit_range()
        {
            var lo = TelegraphResolve.Resolve(-5f, TelegraphStyle.Generic);
            var hi = TelegraphResolve.Resolve(5f, TelegraphStyle.Generic);
            Assert.Equal(TelegraphResolve.Resolve(0f, TelegraphStyle.Generic).FillFraction, lo.FillFraction, 5);
            Assert.Equal(TelegraphResolve.Resolve(1f, TelegraphStyle.Generic).FillFraction, hi.FillFraction, 5);
        }

        [Fact]
        public void FillSweep_grows_fill_fraction_with_progress()
        {
            var a = TelegraphResolve.Resolve(0.1f, TelegraphStyle.Generic);
            var b = TelegraphResolve.Resolve(0.9f, TelegraphStyle.Generic);
            Assert.True(b.FillFraction > a.FillFraction);
            Assert.InRange(a.FillFraction, 0f, 1f);
            Assert.InRange(b.FillFraction, 0f, 1f);
        }

        [Fact]
        public void Without_FillSweep_fill_fraction_is_full()
        {
            var style = TelegraphStyle.Generic;
            style.Animation = TelegraphAnim.None;
            Assert.Equal(1f, TelegraphResolve.Resolve(0f, style).FillFraction, 5);
        }

        [Fact]
        public void ColorRamp_lerps_fill_toward_danger_color()
        {
            var style = TelegraphStyle.Generic;
            var early = TelegraphResolve.Resolve(0f, style);
            var late = TelegraphResolve.Resolve(1f, style);
            // R channel: danger (1.0) vs base (0.95). Late should be at/above early and reach danger at progress 1.
            Assert.True(late.FillColor.R >= early.FillColor.R);
            Assert.Equal(style.DangerColor.R, late.FillColor.R, 3);
        }

        [Fact]
        public void ImpactFlash_spikes_near_one_and_is_zero_early()
        {
            var early = TelegraphResolve.Resolve(0.2f, TelegraphStyle.Generic);
            var late = TelegraphResolve.Resolve(1f, TelegraphStyle.Generic);
            Assert.Equal(0f, early.FlashAdd, 3);
            Assert.True(late.FlashAdd > 0.5f);
        }

        [Fact]
        public void OutlinePulse_oscillates_outline_alpha()
        {
            var style = TelegraphStyle.Poison; // has OutlinePulse, no ImpactFlash
            float a0 = TelegraphResolve.Resolve(0.0f, style).OutlineColor.A;
            float a1 = TelegraphResolve.Resolve(0.25f, style).OutlineColor.A;
            Assert.NotEqual(a0, a1, 3);
        }

        [Fact]
        public void Opacity_scales_all_alphas()
        {
            var full = TelegraphStyle.Generic;
            var half = TelegraphStyle.Generic; half.Opacity = 0.5f;
            var rf = TelegraphResolve.Resolve(0.5f, full);
            var rh = TelegraphResolve.Resolve(0.5f, half);
            Assert.Equal(rf.FillColor.A * 0.5f, rh.FillColor.A, 3);
        }

        [Fact]
        public void Resolve_is_pure()
        {
            var a = TelegraphResolve.Resolve(0.37f, TelegraphStyle.Fire);
            var b = TelegraphResolve.Resolve(0.37f, TelegraphStyle.Fire);
            Assert.Equal(a.FillColor, b.FillColor);
            Assert.Equal(a.OutlineColor, b.OutlineColor);
            Assert.Equal(a.FillFraction, b.FillFraction, 6);
            Assert.Equal(a.FlashAdd, b.FlashAdd, 6);
        }

        [Fact]
        public void Rim_glow_zero_without_flag_and_pulsing_with_flag()
        {
            var off = TelegraphStyle.Steel;
            off.Animation &= ~TelegraphAnim.RimGlow;
            Assert.Equal(0f, TelegraphResolve.Resolve(0.5f, off).RimGlow);

            var on = TelegraphStyle.Frost;
            float a = TelegraphResolve.Resolve(0.10f, on).RimGlow;
            float b = TelegraphResolve.Resolve(0.30f, on).RimGlow;
            Assert.True(a > 0f);
            Assert.True(b > 0f);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Sweep_glow_requires_both_flags_and_fades_out_at_completion()
        {
            var s = TelegraphStyle.Nature;
            Assert.True(TelegraphResolve.Resolve(0.5f, s).SweepGlow > 0f);
            Assert.Equal(0f, TelegraphResolve.Resolve(1f, s).SweepGlow, 3);

            var noSweep = s;
            noSweep.Animation &= ~TelegraphAnim.FillSweep;
            Assert.Equal(0f, TelegraphResolve.Resolve(0.5f, noSweep).SweepGlow);
        }

        [Fact]
        public void Sparkle_passes_energy_through_when_flagged()
        {
            Assert.True(TelegraphResolve.Resolve(0.5f, TelegraphStyle.Arcane).Sparkle > 0f);
            Assert.Equal(0f, TelegraphResolve.Resolve(0.5f, TelegraphStyle.Steel).Sparkle);
        }

        [Fact]
        public void Edge_energy_zero_means_default_full_strength()
        {
            var s = TelegraphStyle.Arcane;
            Assert.Equal(0f, s.EdgeEnergy);
            var r = TelegraphResolve.Resolve(0.5f, s);
            Assert.True(r.RimGlow > 0.2f);

            s.EdgeEnergy = 0.5f;
            var half = TelegraphResolve.Resolve(0.5f, s);
            Assert.Equal(r.RimGlow * 0.5f, half.RimGlow, 4);
            Assert.Equal(r.SweepGlow * 0.5f, half.SweepGlow, 4);
            Assert.Equal(r.Sparkle * 0.5f, half.Sparkle, 4);
        }

        [Fact]
        public void Pattern_feather_and_scale_pass_through_resolve()
        {
            var r = TelegraphResolve.Resolve(0.4f, TelegraphStyle.Frost);
            Assert.Equal(TelegraphFillPattern.RadialNoise, r.Pattern);
            Assert.Equal(TelegraphStyle.Frost.PatternSpeed, r.PatternSpeed);
            Assert.Equal(TelegraphStyle.Frost.PatternScale, r.PatternScale);
            Assert.Equal(TelegraphStyle.Frost.FeatherWidth, r.FeatherFraction);
        }

        [Fact]
        public void Legacy_seven_arg_resolved_ctor_still_compiles_with_zero_new_fields()
        {
            var r = new ResolvedTelegraph(Color.White, Color.White, 1f, 0f, 2f,
                FillMode.Fill, TelegraphBlend.Alpha);
            Assert.Equal(0f, r.RimGlow);
            Assert.Equal(TelegraphFillPattern.Solid, r.Pattern);
            Assert.Equal(0f, r.FeatherFraction);
        }
    [Fact]
    public void Sweep_glow_ramps_in_after_the_early_cast_window()
    {
        // The glow band is wider than the tiny early swept region, so full-strength glow at low progress
        // reads as a ball at the shape center. The resolver holds it at zero through the ramp-in window.
        var s = TelegraphStyle.Nature;
        Assert.Equal(0f, TelegraphResolve.Resolve(0.05f, s).SweepGlow);
        Assert.Equal(0f, TelegraphResolve.Resolve(0.08f, s).SweepGlow);
        Assert.True(TelegraphResolve.Resolve(0.15f, s).SweepGlow > 0f);
        Assert.True(TelegraphResolve.Resolve(0.15f, s).SweepGlow < TelegraphResolve.Resolve(0.5f, s).SweepGlow);
    }

    [Fact]
    public void Interior_dim_passes_through_clamped()
    {
        var s = TelegraphStyle.Frost;
        Assert.Equal(s.InteriorDim, TelegraphResolve.Resolve(0.4f, s).InteriorDim);
        s.InteriorDim = 3f;
        Assert.Equal(1f, TelegraphResolve.Resolve(0.4f, s).InteriorDim);
        s.InteriorDim = -1f;
        Assert.Equal(0f, TelegraphResolve.Resolve(0.4f, s).InteriorDim);
    }

    [Fact]
    public void Runner_follows_the_outline_runner_flag_and_energy()
    {
        Assert.True(TelegraphResolve.Resolve(0.5f, TelegraphStyle.Arcane).Runner > 0f);
        Assert.Equal(0f, TelegraphResolve.Resolve(0.5f, TelegraphStyle.Frost).Runner);
        var s = TelegraphStyle.Steel;
        s.EdgeEnergy = 0.5f;
        Assert.Equal(0.5f, TelegraphResolve.Resolve(0.5f, s).Runner, 4);
    }

    [Fact]
    public void Prior_fourteen_arg_resolved_ctor_still_compiles_with_zero_new_fields()
    {
        var r = new ResolvedTelegraph(Color.White, Color.White, 1f, 0f, 2f,
            FillMode.Fill, TelegraphBlend.Alpha,
            0.1f, TelegraphFillPattern.ScrollingNoise, 1f, 6f, 1f, 1f, 1f);
        Assert.Equal(0f, r.InteriorDim);
        Assert.Equal(0f, r.Runner);
    }

    [Fact]
    public void Fill_mode_silences_the_outline_and_its_band_effects()
    {
        var s = TelegraphStyle.Arcane;
        s.FillMode = FillMode.Fill;
        var r = TelegraphResolve.Resolve(0.5f, s);
        Assert.Equal(0f, r.OutlineColor.A);
        Assert.Equal(0f, r.RimGlow);
        Assert.Equal(0f, r.Runner);
        Assert.True(r.FillColor.A > 0f);
        Assert.True(r.SweepGlow > 0f);
    }

    [Fact]
    public void Outline_mode_silences_the_fill()
    {
        var s = TelegraphStyle.Generic;
        s.FillMode = FillMode.Outline;
        var r = TelegraphResolve.Resolve(0.5f, s);
        Assert.Equal(0f, r.FillColor.A);
        Assert.True(r.OutlineColor.A > 0f);
    }

    [Fact]
    public void Outline_and_fill_mode_keeps_both_alphas()
    {
        var r = TelegraphResolve.Resolve(0.5f, TelegraphStyle.Generic);
        Assert.True(r.FillColor.A > 0f);
        Assert.True(r.OutlineColor.A > 0f);
    }

    [Fact]
    public void Base_fill_passes_through_clamped()
    {
        var s = TelegraphStyle.Frost;
        Assert.Equal(s.BaseFill, TelegraphResolve.Resolve(0.4f, s).BaseFill);
        s.BaseFill = 2f;
        Assert.Equal(1f, TelegraphResolve.Resolve(0.4f, s).BaseFill);
    }

    [Fact]
    public void Prior_sixteen_arg_resolved_ctor_still_compiles_with_zero_base_fill()
    {
        var r = new ResolvedTelegraph(Color.White, Color.White, 1f, 0f, 2f,
            FillMode.Fill, TelegraphBlend.Alpha,
            0.1f, TelegraphFillPattern.ScrollingNoise, 1f, 6f, 1f, 1f, 1f, 0.5f, 1f);
        Assert.Equal(0f, r.BaseFill);
    }

        [Fact]
        public void World_edge_overrides_carry_through_resolve_and_clamp_negatives()
        {
            var s = TelegraphStyle.Generic;
            s.EdgeWidthWorld = 0.05f;
            s.FeatherWidthWorld = 0.03f;
            var r = TelegraphResolve.Resolve(0.5f, s);
            Assert.Equal(0.05f, r.EdgeWidthWorld, 4);
            Assert.Equal(0.03f, r.FeatherWidthWorld, 4);

            s.EdgeWidthWorld = -1f;
            s.FeatherWidthWorld = -1f;
            r = TelegraphResolve.Resolve(0.5f, s);
            Assert.Equal(0f, r.EdgeWidthWorld);
            Assert.Equal(0f, r.FeatherWidthWorld);
        }

        [Fact]
        public void Void_fallback_carries_through_resolve_and_clamps_the_dim()
        {
            var s = TelegraphStyle.Generic;
            s.VoidFallback = true;
            s.VoidDim = 0.15f;
            var r = TelegraphResolve.Resolve(0.5f, s);
            Assert.True(r.VoidFallback);
            Assert.Equal(0.15f, r.VoidDim, 4);

            s.VoidDim = -1f;
            Assert.Equal(0f, TelegraphResolve.Resolve(0.5f, s).VoidDim);
            s.VoidDim = 3f;
            Assert.Equal(1f, TelegraphResolve.Resolve(0.5f, s).VoidDim);
        }

        [Fact]
        public void Void_fallback_defaults_off_on_every_preset()
        {
            // The whole design leans on this: an existing style renders exactly as it did. A preset that quietly
            // opted in would silently change every consumer's telegraphs over the void.
            foreach (var s in new[]
            {
                TelegraphStyle.Generic, TelegraphStyle.Fire, TelegraphStyle.Poison, TelegraphStyle.Steel,
                TelegraphStyle.Frost, TelegraphStyle.Nature, TelegraphStyle.Arcane,
            })
            {
                var r = TelegraphResolve.Resolve(0.5f, s);
                Assert.False(r.VoidFallback);
                Assert.Equal(0f, r.VoidDim);
            }
        }

        [Fact]
        public void Void_fallback_is_independent_of_progress()
        {
            // A passthrough knob, not an animated one: it must not wobble with the cast, or a ring would pop in and
            // out of the void across the window.
            var s = TelegraphStyle.Generic;
            s.VoidFallback = true;
            s.VoidDim = 0.2f;
            foreach (float p in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
            {
                var r = TelegraphResolve.Resolve(p, s);
                Assert.True(r.VoidFallback);
                Assert.Equal(0.2f, r.VoidDim, 4);
            }
        }

        /// <summary>
        /// The construction shape #126 asked for: every member is init-settable, so a caller names each one
        /// instead of counting positions through a run of ten same-typed floats the compiler cannot order-check.
        /// Distinct values throughout, so a member wired to the wrong backing slot shows up as a mismatch rather
        /// than as two zeros agreeing with each other.
        /// </summary>
        [Fact]
        public void Object_initializer_sets_every_member_by_name()
        {
            var r = new ResolvedTelegraph
            {
                FillColor = new Color(0.1f, 0.2f, 0.3f, 0.4f),
                OutlineColor = new Color(0.5f, 0.6f, 0.7f, 0.8f),
                FillFraction = 0.11f,
                FlashAdd = 0.12f,
                EdgeThickness = 3.5f,
                FillMode = FillMode.OutlineAndFill,
                Blend = TelegraphBlend.Additive,
                FeatherFraction = 0.13f,
                Pattern = TelegraphFillPattern.RadialNoise,
                PatternSpeed = 0.14f,
                PatternScale = 7.5f,
                RimGlow = 0.15f,
                SweepGlow = 0.16f,
                Sparkle = 0.17f,
                InteriorDim = 0.18f,
                Runner = 0.19f,
                BaseFill = 0.21f,
                EdgeWidthWorld = 0.22f,
                FeatherWidthWorld = 0.23f,
                VoidFallback = true,
                VoidDim = 0.24f,
            };

            Assert.Equal(new Color(0.1f, 0.2f, 0.3f, 0.4f), r.FillColor);
            Assert.Equal(new Color(0.5f, 0.6f, 0.7f, 0.8f), r.OutlineColor);
            Assert.Equal(0.11f, r.FillFraction, 5);
            Assert.Equal(0.12f, r.FlashAdd, 5);
            Assert.Equal(3.5f, r.EdgeThickness, 5);
            Assert.Equal(FillMode.OutlineAndFill, r.FillMode);
            Assert.Equal(TelegraphBlend.Additive, r.Blend);
            Assert.Equal(0.13f, r.FeatherFraction, 5);
            Assert.Equal(TelegraphFillPattern.RadialNoise, r.Pattern);
            Assert.Equal(0.14f, r.PatternSpeed, 5);
            Assert.Equal(7.5f, r.PatternScale, 5);
            Assert.Equal(0.15f, r.RimGlow, 5);
            Assert.Equal(0.16f, r.SweepGlow, 5);
            Assert.Equal(0.17f, r.Sparkle, 5);
            Assert.Equal(0.18f, r.InteriorDim, 5);
            Assert.Equal(0.19f, r.Runner, 5);
            Assert.Equal(0.21f, r.BaseFill, 5);
            Assert.Equal(0.22f, r.EdgeWidthWorld, 5);
            Assert.Equal(0.23f, r.FeatherWidthWorld, 5);
            Assert.True(r.VoidFallback);
            Assert.Equal(0.24f, r.VoidDim, 5);
        }

        /// <summary>An initializer that names nothing is the all-inert value, which is what makes a partial
        /// initializer complete: the members it leaves out are exactly the ones the widest constructor used to
        /// take as trailing zeros.</summary>
        [Fact]
        public void An_empty_initializer_is_the_inert_value()
        {
            var r = new ResolvedTelegraph();

            Assert.Equal(0f, r.RimGlow);
            Assert.Equal(0f, r.SweepGlow);
            Assert.Equal(0f, r.Sparkle);
            Assert.Equal(0f, r.Runner);
            Assert.Equal(0f, r.BaseFill);
            Assert.Equal(0f, r.EdgeWidthWorld);
            Assert.Equal(0f, r.FeatherWidthWorld);
            Assert.Equal(0f, r.VoidDim);
            Assert.False(r.VoidFallback);
            Assert.Equal(TelegraphFillPattern.Solid, r.Pattern);
        }

        /// <summary>
        /// The transposition guard on the one production call site: every passthrough gets its own distinct
        /// value, so a pair swapped inside Resolve fails here instead of silently drawing the wrong glow. The
        /// flag-driven terms are separated by which flags are on rather than by value: RimGlow and EdgeSparkle
        /// are set and OutlineRunner is not, so Runner has to be the zero and the other two must not be.
        /// </summary>
        [Fact]
        public void Resolve_carries_each_style_input_to_its_own_member()
        {
            var s = TelegraphStyle.Generic;
            s.FillMode = FillMode.OutlineAndFill;
            s.Blend = TelegraphBlend.Additive;
            s.Animation = TelegraphAnim.RimGlow | TelegraphAnim.EdgeSparkle;   // no OutlineRunner, no FillSweep
            s.EdgeEnergy = 0.5f;
            s.EdgeThickness = 3.25f;
            s.FeatherWidth = 0.11f;
            s.Pattern = TelegraphFillPattern.RadialNoise;
            s.PatternSpeed = 0.22f;
            s.PatternScale = 8.5f;
            s.InteriorDim = 0.33f;
            s.BaseFill = 0.44f;
            s.EdgeWidthWorld = 0.055f;
            s.FeatherWidthWorld = 0.066f;
            s.VoidFallback = true;
            s.VoidDim = 0.77f;

            var r = TelegraphResolve.Resolve(0.5f, s);

            Assert.Equal(3.25f, r.EdgeThickness, 5);
            Assert.Equal(FillMode.OutlineAndFill, r.FillMode);
            Assert.Equal(TelegraphBlend.Additive, r.Blend);
            Assert.Equal(0.11f, r.FeatherFraction, 5);
            Assert.Equal(TelegraphFillPattern.RadialNoise, r.Pattern);
            Assert.Equal(0.22f, r.PatternSpeed, 5);
            Assert.Equal(8.5f, r.PatternScale, 5);
            Assert.Equal(0.33f, r.InteriorDim, 5);
            Assert.Equal(0.44f, r.BaseFill, 5);
            Assert.Equal(0.055f, r.EdgeWidthWorld, 5);
            Assert.Equal(0.066f, r.FeatherWidthWorld, 5);
            Assert.True(r.VoidFallback);
            Assert.Equal(0.77f, r.VoidDim, 5);

            Assert.True(r.RimGlow > 0f);
            Assert.Equal(0.5f, r.Sparkle, 5);      // energy straight through when EdgeSparkle is on
            Assert.Equal(0f, r.Runner);            // OutlineRunner is off
            Assert.Equal(0f, r.SweepGlow);         // SweepGlow needs FillSweep too
        }
    }
}
