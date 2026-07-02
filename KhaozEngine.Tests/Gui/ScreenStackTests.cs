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
        public void Services_round_trips_and_a_screen_reads_it_via_manager()
        {
            var services = new FakeServices();
            var stack = new ScreenStack { Services = services };
            Assert.Same(services, stack.Services);

            var s = new FakeScreen();
            stack.Add(s);
            Assert.Same(services, s.Services); // screen reads its manager's Services
        }

        [Fact]
        public void Screen_services_is_null_when_stack_has_none()
        {
            var stack = new ScreenStack();
            var s = new FakeScreen();
            stack.Add(s);
            Assert.Null(s.Services);
        }

        sealed class FakeServices : System.IServiceProvider
        {
            public object? GetService(System.Type serviceType) => null;
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

        [Fact]
        public void Add_preserves_insertion_order_among_equal_DrawOrder()
        {
            var stack = new ScreenStack();
            var added = new FakeScreen[64];
            for (int i = 0; i < added.Length; i++)
            {
                added[i] = new FakeScreen { DrawOrder = 0 };
                stack.Add(added[i]);
            }

            // Equal DrawOrder must keep insertion order, so the last-added is the topmost (last index).
            for (int i = 0; i < added.Length; i++)
                Assert.Same(added[i], stack.Screens[i]);
            Assert.Same(added[^1], stack.Screens[^1]);
        }

        [Fact]
        public void Add_sorts_by_DrawOrder_then_breaks_ties_by_insertion_order()
        {
            var stack = new ScreenStack();
            // Interleave two DrawOrder groups, added in a known order within each group.
            var low = new FakeScreen[20];
            var high = new FakeScreen[20];
            for (int i = 0; i < 20; i++)
            {
                low[i] = new FakeScreen { DrawOrder = 0 };
                high[i] = new FakeScreen { DrawOrder = 10 };
                stack.Add(low[i]);
                stack.Add(high[i]);
            }

            // All DrawOrder=0 come first (in insertion order), then all DrawOrder=10 (in insertion order).
            for (int i = 0; i < 20; i++)
            {
                Assert.Same(low[i], stack.Screens[i]);
                Assert.Same(high[i], stack.Screens[20 + i]);
            }
        }
    }
}
