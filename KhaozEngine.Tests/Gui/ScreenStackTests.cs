using KhaozEngine.Gui;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class ScreenStackTests
    {
        sealed class FakeScreen : Screen
        {
            public int UpdateCount;
            public bool LastReceivedInput;
            public bool ConsumeInput;
            public override bool Update(float dt, bool receivesInput)
            {
                UpdateCount++;
                LastReceivedInput = receivesInput;
                return ConsumeInput && receivesInput;
            }
            public override void Draw(SpriteBatch batch) { }
        }

        [Fact]
        public void Top_screen_consuming_input_blocks_lower_screens()
        {
            var stack = new ScreenStack();
            var low = new FakeScreen { DrawOrder = 0, PassUpdateThrough = true };
            var high = new FakeScreen { DrawOrder = 10, PassUpdateThrough = true, ConsumeInput = true };
            stack.Add(low); stack.Add(high);

            stack.Update(0.016f, InputState.Empty);

            Assert.True(high.LastReceivedInput);   // topmost gets input
            Assert.False(low.LastReceivedInput);   // blocked because high consumed it
            Assert.Equal(1, low.UpdateCount);      // but still updated (PassUpdateThrough)
        }

        [Fact]
        public void Modal_screen_stops_lower_screens_updating()
        {
            var stack = new ScreenStack();
            var low = new FakeScreen { DrawOrder = 0, PassUpdateThrough = true };
            var modal = new FakeScreen { DrawOrder = 10, PassUpdateThrough = false };
            stack.Add(low); stack.Add(modal);

            stack.Update(0.016f, InputState.Empty);

            Assert.Equal(1, modal.UpdateCount);
            Assert.Equal(0, low.UpdateCount);      // modal stopped the loop
        }

        [Fact]
        public void AlwaysReceivesInput_screen_gets_input_even_when_a_higher_screen_consumed()
        {
            var stack = new ScreenStack();
            var nav = new FakeScreen { DrawOrder = 0, PassUpdateThrough = true, AlwaysReceivesInput = true };
            var top = new FakeScreen { DrawOrder = 10, PassUpdateThrough = true, ConsumeInput = true };
            stack.Add(nav); stack.Add(top);

            stack.Update(0.016f, InputState.Empty);

            Assert.True(nav.LastReceivedInput);
        }

        [Fact]
        public void Transition_on_progresses_then_goes_active()
        {
            var stack = new ScreenStack();
            var s = new FakeScreen { TransitionOnDuration = 0.1f };
            stack.Add(s);
            Assert.Equal(ScreenState.TransitionOn, s.State);
            Assert.Equal(0f, s.TransitionAlpha);

            stack.Update(0.05f, InputState.Empty);   // halfway
            Assert.Equal(ScreenState.TransitionOn, s.State);
            Assert.True(s.TransitionAlpha > 0.4f && s.TransitionAlpha < 0.6f, s.TransitionAlpha.ToString());

            stack.Update(0.1f, InputState.Empty);    // completes
            Assert.Equal(ScreenState.Active, s.State);
            Assert.Equal(1f, s.TransitionAlpha);
        }

        [Fact]
        public void ExitScreen_with_transition_animates_out_then_removes()
        {
            var stack = new ScreenStack();
            var s = new FakeScreen { TransitionOffDuration = 0.1f };
            stack.Add(s);
            s.ExitScreen();
            Assert.True(s.IsExiting);

            stack.Update(0.05f, InputState.Empty);
            Assert.Contains(s, stack.Screens);       // still animating out

            stack.Update(0.1f, InputState.Empty);
            Assert.DoesNotContain(s, stack.Screens); // removed when the out-transition completes
        }
    }
}
