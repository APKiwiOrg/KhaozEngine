using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public sealed class RadialMenuValidationTests
    {
        static readonly Rect Safe = new(0f, 0f, 960f, 540f);
        static readonly Vector2 Center = new(480f, 270f);

        [Fact]
        public void Every_geometry_helper_rejects_default_metrics()
        {
            RadialMenuMetrics metrics = default;

            Assert.Throws<ArgumentOutOfRangeException>(() => RadialMenu.WedgeAngles(0, 4, metrics));
            Assert.Throws<ArgumentOutOfRangeException>(() => RadialMenu.ComputeCenter(Center, Safe, 4, 2, metrics));
            Assert.Throws<ArgumentOutOfRangeException>(() => RadialMenu.ComputeBounds(Center, 4, 2, metrics));
            Assert.Throws<ArgumentOutOfRangeException>(() => RadialMenu.EntryAt(Center, Center, 4, metrics));
            Assert.Throws<ArgumentOutOfRangeException>(() => RadialMenu.ChoiceBounds(Center, 2, 0, metrics));
            Assert.Throws<ArgumentOutOfRangeException>(() => RadialMenu.LabelPoint(Center, 0, 4, metrics));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(10)]
        [InlineData(11)]
        [InlineData(12)]
        [InlineData(13)]
        public void Metrics_setter_rejects_a_nan_in_every_field(int field)
        {
            var menu = new RadialMenu();
            RadialMenuMetrics before = menu.Metrics;

            Assert.Throws<ArgumentOutOfRangeException>(() => menu.Metrics = MetricsWith(field, float.NaN));
            Assert.Equal(before, menu.Metrics);
        }

        [Fact]
        public void Metrics_setter_rejects_positive_and_negative_infinity()
        {
            var menu = new RadialMenu();

            Assert.Throws<ArgumentOutOfRangeException>(() => menu.Metrics =
                RadialMenuMetrics.Default with { OuterRadius = float.PositiveInfinity });
            Assert.Throws<ArgumentOutOfRangeException>(() => menu.Metrics =
                RadialMenuMetrics.Default with { ShadowOffset = new Vector2(0f, float.NegativeInfinity) });
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(10)]
        [InlineData(11)]
        public void Metrics_setter_rejects_values_outside_each_range(int field)
        {
            var menu = new RadialMenu();
            RadialMenuMetrics before = menu.Metrics;

            Assert.Throws<ArgumentOutOfRangeException>(() => menu.Metrics = MetricsOutsideRange(field));
            Assert.Equal(before, menu.Metrics);
        }

        [Fact]
        public void Wedge_gap_must_be_smaller_than_the_active_entry_step()
        {
            float step = MathF.Tau / 4f;
            RadialMenuMetrics excessive = RadialMenuMetrics.Default with { WedgeGap = step };

            Assert.Throws<ArgumentOutOfRangeException>(() => RadialMenu.WedgeAngles(0, 4, excessive));

            var menu = new RadialMenu { SafeBounds = Safe, Metrics = excessive };
            Assert.Throws<ArgumentOutOfRangeException>(() => menu.Open(
                LocalizedText.Raw("Too wide"), Entries(4), Center));

            RadialMenuMetrics exactValidEdge = excessive with { WedgeGap = MathF.BitDecrement(step) };
            RadialMenu.WedgeAngles(0, 4, exactValidEdge);
        }

        [Fact]
        public void Geometry_helpers_reject_nonfinite_points_and_centres()
        {
            Vector2 nan = new(float.NaN, 0f);
            Vector2 infinity = new(0f, float.PositiveInfinity);
            RadialMenuMetrics metrics = RadialMenuMetrics.Default;

            Assert.Throws<ArgumentOutOfRangeException>(() => RadialMenu.ComputeCenter(nan, Safe, 4, 2, metrics));
            Assert.Throws<ArgumentOutOfRangeException>(() => RadialMenu.ComputeBounds(infinity, 4, 2, metrics));
            Assert.Throws<ArgumentOutOfRangeException>(() => RadialMenu.EntryAt(nan, Center, 4, metrics));
            Assert.Throws<ArgumentOutOfRangeException>(() => RadialMenu.EntryAt(Center, infinity, 4, metrics));
            Assert.Throws<ArgumentOutOfRangeException>(() => RadialMenu.ChoiceBounds(nan, 2, 0, metrics));
            Assert.Throws<ArgumentOutOfRangeException>(() => RadialMenu.LabelPoint(infinity, 0, 4, metrics));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void Safe_rectangles_reject_nonfinite_coordinates_and_negative_dimensions(int field)
        {
            Rect invalid = InvalidSafeRectangle(field);
            var menu = new RadialMenu { SafeBounds = Safe };

            Assert.Throws<ArgumentOutOfRangeException>(() => menu.SafeBounds = invalid);
            Assert.Equal(Safe, menu.SafeBounds);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                RadialMenu.ComputeCenter(Center, invalid, 4, 2, RadialMenuMetrics.Default));
        }

        [Fact]
        public void ComputeCenter_and_Open_reject_a_safe_area_smaller_than_the_composition()
        {
            var tooNarrow = new Rect(0f, 0f, 311f, 352f);
            var tooShort = new Rect(0f, 0f, 312f, 351f);

            Assert.Throws<ArgumentOutOfRangeException>(() => RadialMenu.ComputeCenter(
                Center, tooNarrow, 4, 4, RadialMenuMetrics.Default));
            Assert.Throws<ArgumentOutOfRangeException>(() => RadialMenu.ComputeCenter(
                Center, tooShort, 4, 4, RadialMenuMetrics.Default));

            var menu = new RadialMenu { SafeBounds = tooNarrow };
            Assert.Throws<ArgumentOutOfRangeException>(() => menu.Open(
                LocalizedText.Raw("Too small"), Entries(4), Center, Choices(4)));
            Assert.False(menu.IsOpen);
        }

        [Fact]
        public void Exact_minimum_safe_area_is_valid_and_deterministic()
        {
            var exact = new Rect(0f, 0f, 312f, 352f);

            Vector2 center = RadialMenu.ComputeCenter(
                new Vector2(500f, 500f), exact, 4, 4, RadialMenuMetrics.Default);
            Rect bounds = RadialMenu.ComputeBounds(center, 4, 4, RadialMenuMetrics.Default);

            Assert.Equal(new Rect(8f, 8f, 296f, 336f), bounds);
        }

        [Fact]
        public void Exact_nonnegative_metric_edges_remain_valid()
        {
            RadialMenuMetrics metrics = RadialMenuMetrics.Default with
            {
                InnerRadius = 0f,
                OuterRadius = 1f,
                WedgeGap = 0f,
                IconSize = 0f,
                LabelScale = 0.001f,
                DetailGap = 0f,
                FooterGap = 0f,
                FooterButtonSize = new Vector2(0.001f, 0.001f),
                Margin = 0f,
                BorderThickness = 0f,
                ShadowOffset = new Vector2(-5f, 5f),
                SheenSpeed = 0f,
            };
            var menu = new RadialMenu { SafeBounds = Safe, Metrics = metrics };

            menu.Open(LocalizedText.Raw("Edges"), Entries(8), Center, Choices(8));

            Assert.Equal(Center, menu.Center);
            Assert.Equal(-1, RadialMenu.EntryAt(Center, Center, 8, metrics));
        }

        [Fact]
        public void Invalid_Open_geometry_leaves_the_existing_menu_unchanged()
        {
            var menu = new RadialMenu { SafeBounds = Safe };
            menu.Open(LocalizedText.Raw("Existing"), Entries(4), Center, Choices(2));
            MenuState before = Snapshot(menu);

            Assert.Throws<ArgumentOutOfRangeException>(() => menu.Open(
                LocalizedText.Raw("Replacement"), Entries(2),
                new Vector2(float.NaN, 0f), Choices(1)));

            Assert.Equal(before, Snapshot(menu));
        }

        static RadialMenuMetrics MetricsWith(int field, float value)
        {
            RadialMenuMetrics metrics = RadialMenuMetrics.Default;
            return field switch
            {
                0 => metrics with { InnerRadius = value },
                1 => metrics with { OuterRadius = value },
                2 => metrics with { WedgeGap = value },
                3 => metrics with { IconSize = value },
                4 => metrics with { LabelScale = value },
                5 => metrics with { DetailGap = value },
                6 => metrics with { FooterGap = value },
                7 => metrics with { FooterButtonSize = new Vector2(value, metrics.FooterButtonSize.Y) },
                8 => metrics with { FooterButtonSize = new Vector2(metrics.FooterButtonSize.X, value) },
                9 => metrics with { Margin = value },
                10 => metrics with { BorderThickness = value },
                11 => metrics with { ShadowOffset = new Vector2(value, metrics.ShadowOffset.Y) },
                12 => metrics with { ShadowOffset = new Vector2(metrics.ShadowOffset.X, value) },
                13 => metrics with { SheenSpeed = value },
                _ => throw new ArgumentOutOfRangeException(nameof(field)),
            };
        }

        static RadialMenuMetrics MetricsOutsideRange(int field)
        {
            RadialMenuMetrics metrics = RadialMenuMetrics.Default;
            return field switch
            {
                0 => metrics with { InnerRadius = -1f },
                1 => metrics with { OuterRadius = metrics.InnerRadius },
                2 => metrics with { WedgeGap = -0.01f },
                3 => metrics with { IconSize = -1f },
                4 => metrics with { LabelScale = 0f },
                5 => metrics with { DetailGap = -1f },
                6 => metrics with { FooterGap = -1f },
                7 => metrics with { FooterButtonSize = new Vector2(0f, metrics.FooterButtonSize.Y) },
                8 => metrics with { FooterButtonSize = new Vector2(metrics.FooterButtonSize.X, -1f) },
                9 => metrics with { Margin = -1f },
                10 => metrics with { BorderThickness = -1f },
                11 => metrics with { SheenSpeed = -1f },
                _ => throw new ArgumentOutOfRangeException(nameof(field)),
            };
        }

        static Rect InvalidSafeRectangle(int field) => field switch
        {
            0 => new Rect(float.NaN, 0f, 960f, 540f),
            1 => new Rect(0f, float.PositiveInfinity, 960f, 540f),
            2 => new Rect(0f, 0f, float.NaN, 540f),
            3 => new Rect(0f, 0f, 960f, float.NegativeInfinity),
            4 => new Rect(0f, 0f, -1f, 540f),
            5 => new Rect(0f, 0f, 960f, -1f),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        static RadialMenuEntry[] Entries(int count)
        {
            var entries = new RadialMenuEntry[count];
            for (int i = 0; i < count; i++)
                entries[i] = new RadialMenuEntry(LocalizedText.Raw($"Entry {i}"), i + 1);
            return entries;
        }

        static RadialMenuChoice[] Choices(int count)
        {
            var choices = new RadialMenuChoice[count];
            for (int i = 0; i < count; i++)
                choices[i] = new RadialMenuChoice(LocalizedText.Raw($"Choice {i}"), i + 10);
            return choices;
        }

        static MenuState Snapshot(RadialMenu menu) => new(
            menu.IsOpen,
            menu.Metrics,
            menu.SafeBounds,
            menu.Bounds,
            menu.ResolvedTitle,
            menu.ResolvedEntryLabel(0),
            menu.ActiveIndex,
            menu.HoverIndex,
            menu.WasSelected,
            menu.Selection,
            menu.WasChoiceChanged,
            menu.ChoiceChange,
            menu.WasDismissed);

        readonly record struct MenuState(
            bool IsOpen,
            RadialMenuMetrics Metrics,
            Rect SafeBounds,
            Rect Bounds,
            string Title,
            string FirstEntry,
            int ActiveIndex,
            int HoverIndex,
            bool WasSelected,
            RadialMenuSelection Selection,
            bool WasChoiceChanged,
            RadialMenuChoiceChange ChoiceChange,
            bool WasDismissed);
    }
}
