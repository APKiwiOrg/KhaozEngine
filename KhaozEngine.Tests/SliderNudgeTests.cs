using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests
{
    // Slider.Nudge adjusts the value for keyboard / gamepad control (clamped 0..1), independent of pointer drag.
    public sealed class SliderNudgeTests
    {
        static Slider New(float value) => new(new Rect(0, 0, 100, 10), value);

        [Fact]
        public void Nudge_moves_the_value_and_reports_change()
        {
            var s = New(0.5f);
            Assert.True(s.Nudge(0.1f));
            Assert.Equal(0.6f, s.Value, 3);
            Assert.True(s.Nudge(-0.2f));
            Assert.Equal(0.4f, s.Value, 3);
        }

        [Fact]
        public void Nudge_clamps_to_unit_range_and_reports_no_change_at_the_edge()
        {
            var s = New(0.95f);
            Assert.True(s.Nudge(0.2f));
            Assert.Equal(1f, s.Value, 3);
            Assert.False(s.Nudge(0.2f)); // already at max
            Assert.Equal(1f, s.Value, 3);
        }

        [Fact]
        public void Nudge_is_a_noop_when_disabled_or_zero()
        {
            var s = New(0.5f);
            s.Enabled = false;
            Assert.False(s.Nudge(0.3f));
            Assert.Equal(0.5f, s.Value, 3);

            s.Enabled = true;
            Assert.False(s.Nudge(0f));
            Assert.Equal(0.5f, s.Value, 3);
        }
    }
}
