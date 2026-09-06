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

        /// <summary>A visibility-gated modal-overlay fake for exercising <see cref="ScreenStack"/> routing:
        /// recomputes <see cref="Screen.PassUpdateThrough"/> from its own visibility and only consumes while
        /// visible (how a REQUIRED <see cref="UpdateOverlayScreen"/> behaves; an optional one stays non-modal).</summary>
        sealed class FakeVisibilityOverlay : Screen
        {
            public bool Visible;
            public bool LastReceivedInput;
            public override bool Update(float dt, bool receivesInput)
            {
                LastReceivedInput = receivesInput;
                PassUpdateThrough = !Visible; // recomputed every frame (as a required UpdateOverlayScreen is modal)
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
        public void Modal_freezes_lower_transitions_by_default()
        {
            var stack = new ScreenStack();
            var low = new FakeScreen { DrawOrder = 0, TransitionOnDuration = 1f };
            var modal = new FakeScreen { DrawOrder = 10, PassUpdateThrough = false };
            stack.Add(low);
            stack.Add(modal);

            stack.Update(0.5f, InputState.Empty);

            Assert.Equal(ScreenState.TransitionOn, low.State);
            Assert.Equal(0f, low.TransitionAlpha);
            Assert.Equal(0, low.UpdateCount);
        }

        [Fact]
        public void Opt_in_advances_lower_transition_without_updating_or_routing_input()
        {
            var stack = new ScreenStack { AdvanceTransitionsBehindModal = true };
            var low = new FakeScreen { DrawOrder = 0, TransitionOnDuration = 1f };
            var modal = new FakeScreen { DrawOrder = 10, PassUpdateThrough = false };
            stack.Add(low);
            stack.Add(modal);

            stack.Update(0.5f, InputState.Empty);

            Assert.Equal(ScreenState.TransitionOn, low.State);
            Assert.Equal(0.5f, low.TransitionAlpha, 3);
            Assert.Equal(0, low.UpdateCount);
            Assert.False(low.LastReceivedInput);
        }

        [Fact]
        public void Opt_in_removes_a_lower_screen_when_its_transition_off_finishes()
        {
            var stack = new ScreenStack { AdvanceTransitionsBehindModal = true };
            var low = new UnloadTrackingScreen { DrawOrder = 0, TransitionOffDuration = 0.1f };
            var modal = new FakeScreen { DrawOrder = 10, PassUpdateThrough = false };
            stack.Add(low);
            stack.Add(modal);
            low.ExitScreen();

            stack.Update(0.2f, InputState.Empty);

            Assert.DoesNotContain(low, stack.Screens);
            Assert.True(low.Unloaded);
            Assert.Equal(ScreenState.Hidden, low.State);
            Assert.Equal(0, low.UpdatesAfterUnload);
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
        /// A visibility-gated modal overlay (how a REQUIRED <see cref="UpdateOverlayScreen"/> behaves): recomputes
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

        /// <summary>A screen that frees something in <see cref="Screen.UnloadContent"/> and would read it back in
        /// <see cref="Screen.Update"/>. Records the violation rather than throwing, so the failure reads as an
        /// assertion about lifecycle rather than a stack trace out of the stack's own loop.</summary>
        sealed class UnloadTrackingScreen : Screen
        {
            public bool Unloaded;
            public int UpdatesAfterUnload;
            public override void UnloadContent() => Unloaded = true;
            public override bool Update(float dt, bool receivesInput)
            {
                if (Unloaded) UpdatesAfterUnload++;
                return false;
            }
            public override void Draw(SpriteBatch batch) { }
        }

        /// <summary>Removes another screen from inside its own Update, the way a menu screen swaps the screen
        /// under it.</summary>
        sealed class RemoverScreen : Screen
        {
            public Screen? Target;
            public override bool Update(float dt, bool receivesInput)
            {
                if (Target != null) { Manager.Remove(Target); Target = null; }
                return false;
            }
            public override void Draw(SpriteBatch batch) { }
        }

        /// <summary>
        /// The transition-off completion frame removes the screen from inside the update loop, which iterates a
        /// scratch copy taken before that removal. Without a terminal state on the removed screen the loop reaches
        /// it anyway (its state is still TransitionOff, not Hidden) and runs one more Update on a screen whose
        /// UnloadContent has already freed its content. See #102.
        /// </summary>
        [Fact]
        public void Screen_removed_by_its_own_transition_off_is_not_updated_again_that_frame()
        {
            var stack = new ScreenStack();
            var s = new UnloadTrackingScreen { TransitionOffDuration = 0.1f };
            stack.Add(s);
            s.ExitScreen();

            stack.Update(0.05f, InputState.Empty);   // still animating out
            Assert.Contains(s, stack.Screens);
            Assert.False(s.Unloaded);

            stack.Update(0.1f, InputState.Empty);    // completes: unloaded and removed inside this very loop
            Assert.DoesNotContain(s, stack.Screens);
            Assert.True(s.Unloaded);
            Assert.Equal(0, s.UpdatesAfterUnload);
        }

        /// <summary>
        /// The same hazard from the other direction: a screen removed by ANOTHER screen's Update is still in the
        /// scratch copy the loop is walking, so it must not be updated after its content is gone either.
        /// </summary>
        [Fact]
        public void Screen_removed_by_another_screens_update_is_not_updated_that_frame()
        {
            var stack = new ScreenStack();
            var low = new UnloadTrackingScreen { DrawOrder = 0 };
            var remover = new RemoverScreen { DrawOrder = 10, PassUpdateThrough = true, Target = low };
            stack.Add(low); stack.Add(remover);

            stack.Update(0.016f, InputState.Empty);

            Assert.DoesNotContain(low, stack.Screens);
            Assert.True(low.Unloaded);
            Assert.Equal(0, low.UpdatesAfterUnload);
        }

        [Fact]
        public void Remove_leaves_the_screen_in_the_terminal_hidden_state()
        {
            var stack = new ScreenStack();
            var s = new FakeScreen { TransitionOffDuration = 0.1f };
            stack.Add(s);
            s.ExitScreen();
            stack.Update(0.2f, InputState.Empty);    // animates out and removes

            Assert.DoesNotContain(s, stack.Screens);
            Assert.Equal(ScreenState.Hidden, s.State);
            Assert.False(s.IsExiting);               // the exit finished, so the request is spent
        }

        /// <summary>Removal is terminal, not one-way: the Hidden state Remove leaves behind must not turn a
        /// re-add into a screen that sits in the stack invisible and never updated.</summary>
        [Fact]
        public void Re_adding_a_removed_screen_mounts_it_active_again()
        {
            var stack = new ScreenStack();
            var s = new FakeScreen();
            stack.Add(s);
            stack.Remove(s);
            Assert.Equal(ScreenState.Hidden, s.State);

            stack.Add(s);
            Assert.Equal(ScreenState.Active, s.State);

            stack.Update(0.016f, InputState.Empty);
            Assert.Equal(1, s.UpdateCount);
        }

        /// <summary>The other half of the Add state rule: a screen the CALLER pre-set to Hidden, never mounted
        /// anywhere, is still added dormant.</summary>
        [Fact]
        public void A_never_mounted_screen_pre_set_to_hidden_is_added_dormant()
        {
            var stack = new ScreenStack();
            var s = new FakeScreen { State = ScreenState.Hidden };
            stack.Add(s);

            Assert.Equal(ScreenState.Hidden, s.State);
            stack.Update(0.016f, InputState.Empty);
            Assert.Equal(0, s.UpdateCount);
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
