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

    }
}
