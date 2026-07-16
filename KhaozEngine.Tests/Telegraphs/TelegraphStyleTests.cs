using System.Numerics;
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

        [Fact]
        public void New_style_fields_default_to_legacy_zero()
        {
            var s = default(TelegraphStyle);
            Assert.Equal(0f, s.FeatherWidth);
            Assert.Equal(TelegraphFillPattern.Solid, s.Pattern);
            Assert.Equal(0f, s.PatternSpeed);
            Assert.Equal(0f, s.PatternScale);
            Assert.Equal(0f, s.EdgeEnergy);
        }

        [Fact]
        public void Legacy_presets_keep_their_existing_identity()
        {
            Assert.Equal(TelegraphBlend.Alpha, TelegraphStyle.Generic.Blend);
            Assert.Equal(TelegraphBlend.Additive, TelegraphStyle.Fire.Blend);
            Assert.Equal(TelegraphBlend.Alpha, TelegraphStyle.Poison.Blend);
            Assert.Equal(2f, TelegraphStyle.Generic.EdgeThickness);
            Assert.True(TelegraphStyle.Generic.Animation.HasFlag(TelegraphAnim.FillSweep));
            Assert.True(TelegraphStyle.Fire.Animation.HasFlag(TelegraphAnim.OutlinePulse));
        }

        [Fact]
        public void Legacy_presets_gain_modern_rendering()
        {
            Assert.True(TelegraphStyle.Generic.FeatherWidth > 0f);
            Assert.Equal(TelegraphFillPattern.ScrollingNoise, TelegraphStyle.Fire.Pattern);
            Assert.True(TelegraphStyle.Fire.Animation.HasFlag(TelegraphAnim.EdgeSparkle));
            Assert.True(TelegraphStyle.Poison.FeatherWidth > 0f);
        }

        [Theory]
        [InlineData("Steel")]
        [InlineData("Frost")]
        [InlineData("Nature")]
        [InlineData("Arcane")]
        public void Element_presets_are_fully_specified(string name)
        {
            var s = name switch
            {
                "Steel" => TelegraphStyle.Steel,
                "Frost" => TelegraphStyle.Frost,
                "Nature" => TelegraphStyle.Nature,
                _ => TelegraphStyle.Arcane,
            };
            Assert.True(s.Opacity > 0f);
            Assert.True(s.EdgeThickness > 0f);
            Assert.True(s.FeatherWidth > 0f);
            Assert.NotEqual(TelegraphFillPattern.Solid, s.Pattern);
            Assert.True(s.PatternSpeed > 0f);
            Assert.True(s.PatternScale > 0f);
            Assert.True(s.FillColor.A > 0f);
            Assert.True(s.OutlineColor.A > 0f);
            Assert.Equal(FillMode.OutlineAndFill, s.FillMode);
            Assert.Equal(ZoneSense.Danger, s.ZoneSense);
            Assert.True(s.Animation.HasFlag(TelegraphAnim.FillSweep));
        }

        [Fact]
        public void Element_preset_palettes_are_distinct()
        {
            Assert.NotEqual((Vector4)TelegraphStyle.Frost.FillColor, (Vector4)TelegraphStyle.Steel.FillColor);
            Assert.NotEqual((Vector4)TelegraphStyle.Nature.FillColor, (Vector4)TelegraphStyle.Frost.FillColor);
            Assert.NotEqual((Vector4)TelegraphStyle.Arcane.FillColor, (Vector4)TelegraphStyle.Nature.FillColor);
            Assert.Equal(TelegraphBlend.Additive, TelegraphStyle.Arcane.Blend);
            Assert.Equal(TelegraphBlend.Alpha, TelegraphStyle.Frost.Blend);
        }
    [Fact]
    public void Interior_dim_defaults_to_legacy_zero_and_presets_set_it()
    {
        Assert.Equal(0f, default(TelegraphStyle).InteriorDim);
        Assert.True(TelegraphStyle.Generic.InteriorDim > 0f);
        Assert.True(TelegraphStyle.Fire.InteriorDim > 0f);
        Assert.True(TelegraphStyle.Poison.InteriorDim > 0f);
        Assert.True(TelegraphStyle.Steel.InteriorDim > 0f);
        Assert.True(TelegraphStyle.Frost.InteriorDim > 0f);
        Assert.True(TelegraphStyle.Nature.InteriorDim > 0f);
        Assert.True(TelegraphStyle.Arcane.InteriorDim > 0f);
    }

    [Fact]
    public void Outline_runner_flag_is_on_steel_and_arcane_only()
    {
        Assert.True(TelegraphStyle.Steel.Animation.HasFlag(TelegraphAnim.OutlineRunner));
        Assert.True(TelegraphStyle.Arcane.Animation.HasFlag(TelegraphAnim.OutlineRunner));
        Assert.False(TelegraphStyle.Generic.Animation.HasFlag(TelegraphAnim.OutlineRunner));
        Assert.False(TelegraphStyle.Fire.Animation.HasFlag(TelegraphAnim.OutlineRunner));
        Assert.False(TelegraphStyle.Frost.Animation.HasFlag(TelegraphAnim.OutlineRunner));
    }

    [Fact]
    public void Base_fill_defaults_to_legacy_zero_and_presets_set_it()
    {
        Assert.Equal(0f, default(TelegraphStyle).BaseFill);
        Assert.True(TelegraphStyle.Generic.BaseFill > 0f);
        Assert.True(TelegraphStyle.Fire.BaseFill > 0f);
        Assert.True(TelegraphStyle.Frost.BaseFill > 0f);
        Assert.True(TelegraphStyle.Arcane.BaseFill > 0f);
    }

    }
}
