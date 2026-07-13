using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class ProgressBarTests
    {
        static readonly Rect Bar = new(10, 10, 200, 12);

        [Fact]
        public void Fraction_clamps_to_the_unit_range_on_assignment()
        {
            var bar = new ProgressBar(Bar);
            bar.Fraction = 1.5f;
            Assert.Equal(1f, bar.Fraction, 3);
            bar.Fraction = -0.5f;
            Assert.Equal(0f, bar.Fraction, 3);
            bar.Fraction = 0.3f;
            Assert.Equal(0.3f, bar.Fraction, 3);
        }

        [Fact]
        public void Constructor_clamps_the_initial_fraction()
        {
            Assert.Equal(1f, new ProgressBar(Bar, 2f).Fraction, 3);
            Assert.Equal(0f, new ProgressBar(Bar, -1f).Fraction, 3);
            Assert.Equal(0.5f, new ProgressBar(Bar, 0.5f).Fraction, 3);
        }

        [Fact]
        public void FillRect_width_scales_with_fraction_inside_the_border()
        {
            var bar = new ProgressBar(Bar);
            float innerW = bar.InnerBounds.Width;   // Bounds width less the border on both sides
            bar.Fraction = 0.5f;
            Assert.Equal(innerW * 0.5f, bar.FillRect.Width, 3);
            bar.Fraction = 0.25f;
            Assert.Equal(innerW * 0.25f, bar.FillRect.Width, 3);
        }

        [Fact]
        public void FillRect_is_empty_at_zero_and_full_inner_width_at_one()
        {
            var bar = new ProgressBar(Bar);
            bar.Fraction = 0f;
            Assert.Equal(0f, bar.FillRect.Width, 3);
            bar.Fraction = 1f;
            Assert.Equal(bar.InnerBounds.Width, bar.FillRect.Width, 3);
        }

        [Fact]
        public void FillRect_sits_within_the_bar_frame()
        {
            var bar = new ProgressBar(Bar) { Fraction = 1f };
            Rect fill = bar.FillRect;
            Assert.True(fill.X >= Bar.X);
            Assert.True(fill.Y >= Bar.Y);
            Assert.True(fill.Right <= Bar.Right + 1e-3f);
            Assert.True(fill.Bottom <= Bar.Bottom + 1e-3f);
        }

        [Fact]
        public void InnerBounds_insets_by_the_style_border_thickness()
        {
            var bar = new ProgressBar(Bar);
            float bt = bar.Style.BorderThickness;
            Assert.Equal(Bar.X + bt, bar.InnerBounds.X, 3);
            Assert.Equal(Bar.Width - 2f * bt, bar.InnerBounds.Width, 3);
        }
    }
}
