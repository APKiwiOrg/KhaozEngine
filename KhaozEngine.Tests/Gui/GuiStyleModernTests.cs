using System.Numerics;
using KhaozEngine.Gui;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class GuiStyleModernTests
    {
        [Fact]
        public void Default_KeepsTodaysFlatLook()
        {
            var s = GuiStyle.Default;
            Assert.Equal(0f, s.CornerRadius);
            Assert.Equal(0f, s.ShadowSize);
            Assert.Equal(GuiFill.Solid, s.FillMode);
            Assert.Equal(1f, s.GradientTopScale);
            Assert.Equal(1f, s.GradientBottomScale);
            Assert.Equal(0f, s.GlowSize);
            Assert.True(s.IsFlat);   // the plain-path predicate
        }

        [Fact]
        public void Modern_OptsIntoRoundedShadowGradientGlow()
        {
            var s = GuiStyle.Modern;
            Assert.True(s.CornerRadius > 0f);
            Assert.True(s.ShadowSize > 0f);
            Assert.Equal(GuiFill.VerticalGradient, s.FillMode);
            Assert.True(s.GlowSize > 0f);
            Assert.False(s.IsFlat);
        }

        [Fact]
        public void ScaleRgb_MultipliesRgbKeepsAlpha()
        {
            var c = new Vector4(0.4f, 0.5f, 0.6f, 0.8f);
            var scaled = GuiStyle.ScaleRgb(c, 1.5f);
            Assert.Equal(0.6f, scaled.X, 3);
            Assert.Equal(0.75f, scaled.Y, 3);
            Assert.Equal(0.9f, scaled.Z, 3);
            Assert.Equal(0.8f, scaled.W, 3);   // alpha untouched
        }

        [Fact]
        public void ScaleRgb_ClampsToOne()
        {
            var scaled = GuiStyle.ScaleRgb(new Vector4(0.8f, 0.8f, 0.8f, 1f), 2f);
            Assert.Equal(1f, scaled.X, 3);
        }
    }
}
