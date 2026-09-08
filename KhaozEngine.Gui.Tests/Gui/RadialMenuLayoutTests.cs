using System;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public sealed class RadialMenuLayoutTests
    {
        static RadialMenuMetrics Metrics => RadialMenuMetrics.Default;
        static readonly Rect Safe = new(0f, 0f, 960f, 540f);

        [Theory]
        [InlineData(0)]
        [InlineData(9)]
        public void Entry_count_outside_one_through_eight_is_rejected(int count)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => RadialMenu.ValidateEntryCount(count));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(8)]
        public void Entry_count_one_through_eight_is_accepted(int count)
        {
            RadialMenu.ValidateEntryCount(count);
        }

        [Fact]
        public void Footer_rejects_more_than_eight_choices()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => RadialMenu.ValidateChoiceCount(9));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(8)]
        public void Footer_accepts_zero_through_eight_choices(int count)
        {
            RadialMenu.ValidateChoiceCount(count);
        }

        [Fact]
        public void Entry_zero_begins_at_twelve_oclock_and_order_is_clockwise()
        {
            Vector2 centre = new(480f, 270f);

            Assert.Equal(0, RadialMenu.EntryAt(new Vector2(480f, 130f), centre, 4, Metrics));
            Assert.Equal(1, RadialMenu.EntryAt(new Vector2(620f, 270f), centre, 4, Metrics));
            Assert.Equal(2, RadialMenu.EntryAt(new Vector2(480f, 410f), centre, 4, Metrics));
            Assert.Equal(3, RadialMenu.EntryAt(new Vector2(340f, 270f), centre, 4, Metrics));
        }

        [Fact]
        public void Wedge_angles_leave_half_the_gap_on_each_edge()
        {
            (float start, float end) = RadialMenu.WedgeAngles(0, 4, Metrics);

            Assert.Equal(MathF.PI / 2f - Metrics.WedgeGap, end - start, 4);
        }

        [Fact]
        public void Inner_disc_angular_gap_and_beyond_outer_radius_have_no_entry()
        {
            Vector2 centre = new(480f, 270f);
            float betweenZeroAndOne = -MathF.PI / 4f;
            Vector2 inGap = centre + new Vector2(MathF.Cos(betweenZeroAndOne), MathF.Sin(betweenZeroAndOne)) * 100f;

            Assert.Equal(-1, RadialMenu.EntryAt(centre + new Vector2(0f, -53f), centre, 4, Metrics));
            Assert.Equal(-1, RadialMenu.EntryAt(inGap, centre, 4, Metrics));
            Assert.Equal(-1, RadialMenu.EntryAt(centre + new Vector2(0f, -149f), centre, 4, Metrics));
        }

        [Fact]
        public void Exact_inner_radius_is_part_of_the_dead_zone()
        {
            Vector2 centre = new(480f, 270f);

            Assert.Equal(-1, RadialMenu.EntryAt(centre + new Vector2(0f, -54f), centre, 4, Metrics));
        }

        [Fact]
        public void Exact_outer_radius_remains_part_of_a_visible_wedge()
        {
            Vector2 centre = new(480f, 270f);

            Assert.Equal(0, RadialMenu.EntryAt(centre + new Vector2(0f, -148f), centre, 4, Metrics));
        }

        [Fact]
        public void Visible_side_of_canonical_wedge_boundaries_remains_part_of_the_wedge()
        {
            Vector2 centre = Vector2.Zero;
            (float start, float end) = RadialMenu.WedgeAngles(0, 4, Metrics);
            float insideStart = start + 0.000001f;
            float insideEnd = end - 0.000001f;
            Vector2 startPoint = centre + new Vector2(MathF.Cos(insideStart), MathF.Sin(insideStart)) * 100f;
            Vector2 endPoint = centre + new Vector2(MathF.Cos(insideEnd), MathF.Sin(insideEnd)) * 100f;

            int startEntry = RadialMenu.EntryAt(startPoint, centre, 4, Metrics);
            int endEntry = RadialMenu.EntryAt(endPoint, centre, 4, Metrics);
            Assert.True(startEntry == 0 && endEntry == 0,
                $"Expected entry 0 at both boundaries, got {startEntry} and {endEntry}.");
        }

        [Fact]
        public void Point_inside_configured_gap_near_visible_boundary_has_no_entry()
        {
            Vector2 centre = Vector2.Zero;
            (float start, _) = RadialMenu.WedgeAngles(0, 4, Metrics);
            float insideGap = start - 0.0000005f;
            Vector2 point = centre + new Vector2(MathF.Cos(insideGap), MathF.Sin(insideGap)) * 100f;

            Assert.Equal(-1, RadialMenu.EntryAt(point, centre, 4, Metrics));
        }

        [Fact]
        public void Label_point_is_midway_through_the_first_wedge()
        {
            Vector2 centre = new(480f, 270f);

            Assert.Equal(new Vector2(480f, 169f), RadialMenu.LabelPoint(centre, 0, 4, Metrics));
        }

        [Fact]
        public void Choice_bounds_form_a_centred_footer_below_the_wheel()
        {
            Vector2 centre = new(480f, 270f);

            Assert.Equal(new Rect(353f, 428f, 56f, 30f), RadialMenu.ChoiceBounds(centre, 4, 0, Metrics));
            Assert.Equal(new Rect(551f, 428f, 56f, 30f), RadialMenu.ChoiceBounds(centre, 4, 3, Metrics));
        }

        [Fact]
        public void Bounds_include_the_wheel_and_footer()
        {
            Vector2 centre = new(480f, 270f);

            Assert.Equal(new Rect(332f, 122f, 296f, 336f), RadialMenu.ComputeBounds(centre, 4, 4, Metrics));
        }

        [Fact]
        public void Clamp_moves_wheel_and_footer_as_one_composition()
        {
            Vector2 centre = RadialMenu.ComputeCenter(
                new Vector2(955f, 535f), Safe, 8, 4, Metrics);
            Rect bounds = RadialMenu.ComputeBounds(centre, 8, 4, Metrics);

            Assert.True(bounds.X >= Safe.X + Metrics.Margin);
            Assert.True(bounds.Y >= Safe.Y + Metrics.Margin);
            Assert.True(bounds.Right <= Safe.Right - Metrics.Margin);
            Assert.True(bounds.Bottom <= Safe.Bottom - Metrics.Margin);
        }

        [Fact]
        public void Clamp_accounts_for_an_eight_choice_footer_wider_than_the_wheel()
        {
            Vector2 centre = RadialMenu.ComputeCenter(
                new Vector2(5f, 270f), Safe, 4, 8, Metrics);
            Rect bounds = RadialMenu.ComputeBounds(centre, 4, 8, Metrics);

            Assert.Equal(Safe.X + Metrics.Margin, bounds.X);
            Assert.True(bounds.Right <= Safe.Right - Metrics.Margin);
        }

        [Theory]
        [InlineData(0f, 270f)]
        [InlineData(960f, 270f)]
        [InlineData(480f, 0f)]
        [InlineData(480f, 540f)]
        [InlineData(0f, 0f)]
        [InlineData(960f, 0f)]
        [InlineData(0f, 540f)]
        [InlineData(960f, 540f)]
        public void Clamp_keeps_eight_choice_composition_inside_each_edge_and_corner(float x, float y)
        {
            Vector2 centre = RadialMenu.ComputeCenter(new Vector2(x, y), Safe, 8, 8, Metrics);
            Rect bounds = RadialMenu.ComputeBounds(centre, 8, 8, Metrics);

            Assert.True(bounds.X >= Safe.X + Metrics.Margin);
            Assert.True(bounds.Y >= Safe.Y + Metrics.Margin);
            Assert.True(bounds.Right <= Safe.Right - Metrics.Margin);
            Assert.True(bounds.Bottom <= Safe.Bottom - Metrics.Margin);
        }
    }
}
