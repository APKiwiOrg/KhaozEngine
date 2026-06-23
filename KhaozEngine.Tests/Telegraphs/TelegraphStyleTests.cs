using KhaozEngine.Primitives;
using KhaozEngine.Telegraphs;
using Xunit;

namespace KhaozEngine.Tests.Telegraphs
{
    public class TelegraphStyleTests
    {
        [Fact]
        public void Generic_preset_is_alpha_outline_and_fill_with_all_anims()
        {
            var s = TelegraphStyle.Generic;
            Assert.Equal(TelegraphBlend.Alpha, s.Blend);
            Assert.Equal(FillMode.OutlineAndFill, s.FillMode);
            Assert.Equal(ZoneSense.Danger, s.ZoneSense);
            Assert.True(s.Animation.HasFlag(TelegraphAnim.FillSweep));
            Assert.True(s.Animation.HasFlag(TelegraphAnim.ColorRamp));
            Assert.True(s.Animation.HasFlag(TelegraphAnim.ImpactFlash));
        }

        [Fact]
        public void Fire_preset_is_additive()
        {
            Assert.Equal(TelegraphBlend.Additive, TelegraphStyle.Fire.Blend);
        }

        [Fact]
        public void Poison_preset_fill_is_greenish()
        {
            var f = TelegraphStyle.Poison.FillColor;
            Assert.True(f.G > f.R && f.G > f.B);
        }
    }
}
