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
    }
}
