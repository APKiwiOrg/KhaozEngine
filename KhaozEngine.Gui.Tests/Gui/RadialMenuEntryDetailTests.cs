using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public sealed class RadialMenuEntryDetailTests
    {
        static readonly Rect Safe = new(0f, 0f, 400f, 400f);
        static readonly FixedFont Font = new();

        [Fact]
        public void Enabled_detail_under_the_label_is_opt_in()
        {
            RadialMenu menu = OpenEntry(enabled: true);

            Assert.False(menu.ShowsEntryDetailUnderLabel(0));

            menu.ShowEnabledEntryDetails = true;

            Assert.True(menu.ShowsEntryDetailUnderLabel(0));
        }

        [Fact]
        public void Disabled_detail_under_the_label_does_not_require_the_opt_in()
        {
            RadialMenu menu = OpenEntry(enabled: false);

            Assert.False(menu.ShowEnabledEntryDetails);
            Assert.True(menu.ShowsEntryDetailUnderLabel(0));
        }

        [Fact]
        public void Enabled_detail_layout_reuses_width_fitting_and_the_configured_detail_gap()
        {
            RadialMenuMetrics metrics = RadialMenuMetrics.Default with
            {
                LabelScale = 1f,
                DetailGap = 7f,
            };

            RadialMenuEntryTextLayout layout = RadialMenuTextLayout.ComputeEntry(
                Font,
                "A",
                "12345678",
                showDetail: true,
                hasIcon: false,
                point: new Vector2(100f, 100f),
                maximumTextWidth: 20f,
                metrics);

            Assert.Equal(1f, layout.LabelScale);
            Assert.Equal(0.25f, layout.DetailScale);
            Assert.Equal(32f, layout.BlockHeight);
            Assert.Equal(84f, layout.FirstLabelPosition.Y);
            Assert.Equal(111f, layout.DetailPosition.Y);
            Assert.Equal(metrics.DetailGap,
                layout.DetailPosition.Y - (layout.FirstLabelPosition.Y + Font.LineHeight * layout.LabelScale));
        }

        [Fact]
        public void Default_detail_gap_preserves_the_existing_two_pixel_spacing()
        {
            RadialMenuEntryTextLayout layout = RadialMenuTextLayout.ComputeEntry(
                Font,
                "A",
                "B",
                showDetail: true,
                hasIcon: false,
                point: new Vector2(100f, 100f),
                maximumTextWidth: 100f,
                RadialMenuMetrics.Default);

            float labelBottom = layout.FirstLabelPosition.Y + Font.LineHeight * layout.LabelScale;
            Assert.Equal(2f, layout.DetailPosition.Y - labelBottom);
        }

        static RadialMenu OpenEntry(bool enabled)
        {
            var menu = new RadialMenu { SafeBounds = Safe };
            menu.Open(
                LocalizedText.Raw("Actions"),
                [new RadialMenuEntry(
                    LocalizedText.Raw("Drop"),
                    1,
                    Enabled: enabled,
                    Detail: LocalizedText.Raw("(4)"))],
                new Vector2(200f, 200f));
            return menu;
        }

        sealed class FixedFont : ITextMeasurer
        {
            public float LineHeight => 20f;

            public Vector2 Measure(string text) => new(text.Length * 10f, LineHeight);

            public Vector2 Measure(ReadOnlySpan<char> text) => new(text.Length * 10f, LineHeight);
        }
    }
}
