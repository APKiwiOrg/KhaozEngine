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

        /// <summary>The <see cref="UpdateOverlayScreen"/>-style reference pattern for a fake: recomputes
        /// <see cref="Screen.PassUpdateThrough"/> from its own visibility and only consumes while visible.</summary>
        sealed class FakeVisibilityOverlay : Screen
        {
            public bool Visible;
            public bool LastReceivedInput;
            public override bool Update(float dt, bool receivesInput)
            {
                LastReceivedInput = receivesInput;
                PassUpdateThrough = !Visible; // recomputed from visibility every frame, like UpdateOverlayScreen
                return receivesInput && Visible;
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

        /// <summary>
        /// An always-mounted overlay (present in the stack every frame, only sometimes doing something) MUST
        /// return false from <see cref="Screen.Update"/> while dormant. This pins the "dormant overlay" trap
        /// documented on <see cref="Screen.Update"/>: an overlay that instead returns true whenever it merely
        /// RECEIVED input (rather than actually consumed it) silently starves every screen below for as long as
        /// it sits in the stack, which is the exact bug class the contract exists to prevent.
        /// </summary>
        [Fact]
        public void Dormant_always_mounted_overlay_returning_false_does_not_starve_lower_screens()
        {
            var stack = new ScreenStack();
            var low = new FakeScreen { DrawOrder = 0, PassUpdateThrough = true };
            // ConsumeInput defaults to false: a correctly-implemented dormant overlay, present every frame,
            // reporting nothing to do.
            var dormantOverlay = new FakeScreen { DrawOrder = 10, PassUpdateThrough = true, ConsumeInput = false };
            stack.Add(low); stack.Add(dormantOverlay);

            stack.Update(0.016f, InputState.Empty);

            Assert.True(dormantOverlay.LastReceivedInput);
            Assert.True(low.LastReceivedInput);    // not starved by the dormant overlay above it
            Assert.Equal(1, low.UpdateCount);
        }

        /// <summary>
        /// The <see cref="UpdateOverlayScreen"/>-style reference pattern: an overlay that recomputes
        /// <see cref="Screen.PassUpdateThrough"/> from its own visibility and only returns true (consumed) while
        /// actually visible. Confirms both halves of the contract on the same screen across frames: dormant does
        /// not starve, visible/modal does block (and stops the lower screen updating at all, per
        /// <see cref="Modal_screen_stops_lower_screens_updating"/>).
        /// </summary>
        [Fact]
        public void Overlay_only_blocks_lower_screens_while_actually_visible()
        {
            var stack = new ScreenStack();
            var low = new FakeScreen { DrawOrder = 0, PassUpdateThrough = true };
            var overlay = new FakeVisibilityOverlay { DrawOrder = 10, PassUpdateThrough = true, Visible = false };
            stack.Add(low); stack.Add(overlay);

            // Dormant: the overlay is mounted but showing nothing, so the low screen still updates and receives input.
            stack.Update(0.016f, InputState.Empty);
            Assert.True(low.LastReceivedInput);
            Assert.Equal(1, low.UpdateCount);

            // Now visible: the overlay goes modal (PassUpdateThrough flips false) and blocks the low screen entirely.
            overlay.Visible = true;
            stack.Update(0.016f, InputState.Empty);
            Assert.Equal(1, low.UpdateCount); // did not update this frame - blocked by the now-modal overlay
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
