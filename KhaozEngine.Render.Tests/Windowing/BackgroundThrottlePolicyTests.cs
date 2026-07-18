using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    // Pure, headless coverage of the per-frame background-throttle decision (BackgroundThrottlePolicy.Plan): render
    // gating + effective cap across focus / minimize states, for the Default (ON) and Disabled policies plus custom
    // opt-outs. No window / GPU device needed.
    public class BackgroundThrottlePolicyTests
    {
        static readonly BackgroundThrottlePolicy Def = BackgroundThrottlePolicy.Default;
        static readonly BackgroundThrottlePolicy Off = BackgroundThrottlePolicy.Disabled;

        static WindowActivity Focused => new(Focused: true, Minimized: false);
        static WindowActivity Unfocused => new(Focused: false, Minimized: false);
        static WindowActivity Minimized => new(Focused: false, Minimized: true);

        [Fact]
        public void Default_policy_has_the_documented_values()
        {
            Assert.True(Def.ThrottleWhenUnfocused);
            Assert.True(Def.PauseRenderWhenMinimized);
            Assert.Equal(15, Def.UnfocusedHz);
            Assert.Equal(10, Def.MinimizedHz);
            Assert.Equal(15, BackgroundThrottlePolicy.DefaultUnfocusedHz);
            Assert.Equal(10, BackgroundThrottlePolicy.DefaultMinimizedHz);
        }

        [Theory]
        [InlineData(0)]     // base uncapped
        [InlineData(60)]
        [InlineData(144)]
        public void Focused_renders_at_the_base_cap(int baseCap)
        {
            FramePlan plan = Def.Plan(Focused, baseCap);
            Assert.True(plan.RenderAndPresent);
            Assert.Equal(baseCap, plan.CapHz);
        }

        [Fact]
        public void Unfocused_visible_renders_but_caps_to_the_unfocused_rate()
        {
            FramePlan plan = Def.Plan(Unfocused, baseCapHz: 120);
            Assert.True(plan.RenderAndPresent);
            Assert.Equal(15, plan.CapHz); // min(120, 15)
        }

        [Fact]
        public void Unfocused_uses_the_unfocused_rate_when_base_is_uncapped()
        {
            FramePlan plan = Def.Plan(Unfocused, baseCapHz: 0);
            Assert.True(plan.RenderAndPresent);
            Assert.Equal(15, plan.CapHz);
        }

        [Fact]
        public void Unfocused_keeps_a_lower_base_cap_rather_than_raising_it()
        {
            FramePlan plan = Def.Plan(Unfocused, baseCapHz: 10); // already below UnfocusedHz
            Assert.True(plan.RenderAndPresent);
            Assert.Equal(10, plan.CapHz); // min(10, 15)
        }

        [Fact]
        public void Minimized_skips_render_and_idles_at_the_minimized_rate()
        {
            FramePlan plan = Def.Plan(Minimized, baseCapHz: 120);
            Assert.False(plan.RenderAndPresent);
            Assert.Equal(10, plan.CapHz);
        }

        [Fact]
        public void Disabled_policy_never_throttles_or_pauses()
        {
            Assert.Equal(new FramePlan(true, 120), Off.Plan(Focused, 120));
            Assert.Equal(new FramePlan(true, 120), Off.Plan(Unfocused, 120));   // no unfocused throttle
            Assert.Equal(new FramePlan(true, 120), Off.Plan(Minimized, 120));   // renders even minimized
        }

        [Fact]
        public void Custom_render_in_background_but_still_throttle_unfocused()
        {
            // Opt out of the minimized render-pause but keep the unfocused cap: a minimized frame renders, throttled.
            var policy = BackgroundThrottlePolicy.Default with { PauseRenderWhenMinimized = false };
            FramePlan plan = policy.Plan(Minimized, baseCapHz: 120);
            Assert.True(plan.RenderAndPresent);
            Assert.Equal(15, plan.CapHz); // falls through to the unfocused branch (minimized implies unfocused)
        }

        [Fact]
        public void Minimized_idle_falls_back_when_minimized_hz_left_zero()
        {
            var policy = BackgroundThrottlePolicy.Default with { MinimizedHz = 0 };
            // Falls back to the base cap when it is positive...
            Assert.Equal(60, policy.Plan(Minimized, baseCapHz: 60).CapHz);
            // ...else to the default minimized rate so a paused frame never spins uncapped.
            Assert.Equal(BackgroundThrottlePolicy.DefaultMinimizedHz, policy.Plan(Minimized, baseCapHz: 0).CapHz);
        }
    }
}
