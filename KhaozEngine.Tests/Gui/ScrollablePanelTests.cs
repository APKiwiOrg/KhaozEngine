using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    public class ScrollablePanelTests
    {
        // 200x300 panel; 20 items of 40 + 4 spacing -> stride 44, content 880, maxScroll 580.
        static readonly Rect Box = new(100, 100, 200, 300);

        static ScrollablePanel Make() => new(Box) { ItemCount = 20, ItemHeight = 40, ItemSpacing = 4 };

        static InputState Frame(Vector2 pos, bool down, float scroll = 0)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Left);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, new HashSet<MouseButton>(), pos, Vector2.Zero, scroll, 960, 540);
        }

        [Fact]
        public void Max_scroll_is_content_minus_viewport()
        {
            Assert.Equal(580f, Make().MaxScroll, 2);
        }

        [Fact]
        public void Wheel_over_the_panel_scrolls_and_clamps_within_range()
        {
            var sp = Make();
            var p = new Pointer();
            var inside = new Vector2(150, 200);
            p.Update(Frame(inside, false, scroll: -5));   // wheel down 5 notches * 30 = 150
            sp.Update(p, Frame(inside, false, scroll: -5));
            Assert.Equal(150f, sp.ScrollOffset, 2);

            p.Update(Frame(inside, false, scroll: -100));  // overshoot -> clamp to maxScroll
            sp.Update(p, Frame(inside, false, scroll: -100));
            Assert.Equal(580f, sp.ScrollOffset, 2);

            p.Update(Frame(inside, false, scroll: 1000));  // back past the top -> clamp to 0
            sp.Update(p, Frame(inside, false, scroll: 1000));
            Assert.Equal(0f, sp.ScrollOffset, 2);
        }

        [Fact]
        public void Wheel_outside_the_panel_does_not_scroll()
        {
            var sp = Make();
            var p = new Pointer();
            var outside = new Vector2(10, 10);
            p.Update(Frame(outside, false, scroll: -5));
            sp.Update(p, Frame(outside, false, scroll: -5));
            Assert.Equal(0f, sp.ScrollOffset, 2);
        }

        [Fact]
        public void Dragging_inside_scrolls_by_the_pointer_delta()
        {
            var sp = Make();
            var p = new Pointer();
            p.Update(Frame(new Vector2(150, 300), false));
            p.Update(Frame(new Vector2(150, 300), true));   // press inside
            sp.Update(p, Frame(new Vector2(150, 300), true));
            p.Update(Frame(new Vector2(150, 260), true));   // drag up 40px
            sp.Update(p, Frame(new Vector2(150, 260), true));
            Assert.Equal(40f, sp.ScrollOffset, 2);          // content moves up as the finger goes up
        }

        [Fact]
        public void ItemBounds_account_for_the_scroll_offset()
        {
            var sp = Make();
            sp.ScrollTo(44f);                               // one stride
            Assert.Equal(100f - 44f, sp.ItemBounds(0).Y, 2);
            Assert.Equal(100f, sp.ItemBounds(1).Y, 2);      // item 1 slid up into the top
        }

        [Fact]
        public void TappedItemIndex_maps_a_tap_to_the_right_row()
        {
            var sp = Make();
            var p = new Pointer();
            void Tap(Vector2 at)
            {
                p.Update(Frame(at, false));
                p.Update(Frame(at, true));
                p.Update(Frame(at, false));
            }
            Tap(new Vector2(150, 130));                     // y rel 30 -> row 0
            Assert.Equal(0, sp.TappedItemIndex(p));
            Tap(new Vector2(150, 150));                     // y rel 50 -> row 1
            Assert.Equal(1, sp.TappedItemIndex(p));
        }

        [Fact]
        public void Tap_in_the_spacing_gap_returns_no_row()
        {
            var sp = Make();
            var p = new Pointer();
            p.Update(Frame(new Vector2(150, 142), false));  // rel 42, past the 40px item into the 4px gap
            p.Update(Frame(new Vector2(150, 142), true));
            p.Update(Frame(new Vector2(150, 142), false));
            Assert.Equal(-1, sp.TappedItemIndex(p));
        }

        [Fact]
        public void Update_blocks_the_pointer_when_enabled()
        {
            var sp = Make();
            var p = new Pointer();
            p.Update(Frame(new Vector2(150, 200), false));
            sp.Update(p, Frame(new Vector2(150, 200), false));
            Assert.True(p.IsBlocked(new Vector2(150, 200)));
        }

        // ---- opt-in overlay chrome (9.21.0) --------------------------------------------------------------

        [Fact]
        public void Default_current_bounds_equal_the_caller_bounds()
        {
            var sp = Make();   // no header, no slide, no resize
            Assert.Equal(Box.X, sp.CurrentBounds.X, 3);
            Assert.Equal(Box.Y, sp.CurrentBounds.Y, 3);
            Assert.Equal(Box.Width, sp.CurrentBounds.Width, 3);
            Assert.Equal(Box.Height, sp.CurrentBounds.Height, 3);
        }

        [Fact]
        public void Header_band_offsets_the_content_region_and_maxscroll()
        {
            var sp = Make();
            sp.HeaderHeight = 40f;
            // content sits below the 40px header: (100,140,200,260); viewport 260 -> maxScroll 880-260 = 620.
            Assert.Equal(140f, sp.ContentBounds.Y, 3);
            Assert.Equal(260f, sp.ContentBounds.Height, 3);
            Assert.Equal(620f, sp.MaxScroll, 3);
            Assert.Equal(140f, sp.ItemBounds(0).Y, 3);   // first row starts below the header
        }

        [Fact]
        public void Slide_from_bottom_maps_alpha_to_a_vertical_offset_from_the_docked_edge()
        {
            var sp = Make();
            sp.SlideFromBottom = true;
            sp.TransitionAlpha = 1f;
            Assert.Equal(100f, sp.CurrentBounds.Y, 3);   // fully shown at the natural top
            sp.TransitionAlpha = 0.5f;
            Assert.Equal(250f, sp.CurrentBounds.Y, 3);   // half-way down (100 + 0.5*300)
            sp.TransitionAlpha = 0f;
            Assert.Equal(400f, sp.CurrentBounds.Y, 3);   // hidden: docked bottom edge (Bounds.Bottom)
        }

        [Fact]
        public void Dragging_the_header_resizes_within_the_min_max_bounds_docked_to_the_bottom()
        {
            var sp = Make();
            sp.HeaderHeight = 30f;
            sp.Resizable = true;
            sp.MinHeight = 100f;
            sp.MaxHeight = 400f;
            var p = new Pointer();
            // press in the header (y in 100..130), then drag up 50px -> panel grows to 350, top docked at 400-350=50.
            p.Update(Frame(new Vector2(150, 115), false)); sp.Update(p, Frame(new Vector2(150, 115), false));
            p.Update(Frame(new Vector2(150, 115), true));  sp.Update(p, Frame(new Vector2(150, 115), true));
            p.Update(Frame(new Vector2(150, 65), true));   sp.Update(p, Frame(new Vector2(150, 65), true));
            Assert.Equal(350f, sp.CurrentBounds.Height, 3);
            Assert.Equal(50f, sp.CurrentBounds.Y, 3);

            // keep dragging up past the max (staying in-window) -> clamps to MaxHeight 400.
            p.Update(Frame(new Vector2(150, 10), true)); sp.Update(p, Frame(new Vector2(150, 10), true));
            Assert.Equal(400f, sp.CurrentBounds.Height, 3);
        }

        [Fact]
        public void Header_drag_does_not_scroll_the_content()
        {
            var sp = Make();
            sp.HeaderHeight = 30f;
            sp.Resizable = true;
            sp.MinHeight = 100f;
            sp.MaxHeight = 400f;
            var p = new Pointer();
            p.Update(Frame(new Vector2(150, 115), false)); sp.Update(p, Frame(new Vector2(150, 115), false));
            p.Update(Frame(new Vector2(150, 115), true));  sp.Update(p, Frame(new Vector2(150, 115), true));
            p.Update(Frame(new Vector2(150, 165), true));  sp.Update(p, Frame(new Vector2(150, 165), true));
            Assert.Equal(0f, sp.ScrollOffset, 3);   // the header drag resized, it did not pan the list
        }

        [Fact]
        public void Header_drag_up_grows_the_panel_without_scrolling_the_content()
        {
            var sp = Make();
            sp.HeaderHeight = 30f;
            sp.Resizable = true;
            sp.MinHeight = 100f;
            sp.MaxHeight = 400f;
            var p = new Pointer();
            // Press in the header, then drag UP to grow the panel. Growing docks the top upward, so the content
            // region rises past the fixed press-origin - the regression: without the _resizing guard the resize
            // also panned the list (GetDragDelta's press-origin then landed inside the grown ContentBounds).
            p.Update(Frame(new Vector2(150, 115), false)); sp.Update(p, Frame(new Vector2(150, 115), false));
            p.Update(Frame(new Vector2(150, 115), true));  sp.Update(p, Frame(new Vector2(150, 115), true));
            p.Update(Frame(new Vector2(150, 65), true));   sp.Update(p, Frame(new Vector2(150, 65), true));
            Assert.Equal(350f, sp.CurrentBounds.Height, 3);   // it did resize (top rose above the press point)
            Assert.Equal(0f, sp.ScrollOffset, 3);             // and the list did not scroll
        }

        [Fact]
        public void Scrim_tap_outside_the_panel_signals_dismiss_and_reserves_its_footprint()
        {
            var sp = Make();
            sp.Scrim = new Rect(0, 0, 960, 540);
            var p = new Pointer();
            void Tap(Vector2 at)
            {
                p.Update(Frame(at, false)); sp.Update(p, Frame(at, false));
                p.Update(Frame(at, true));  sp.Update(p, Frame(at, true));
                p.Update(Frame(at, false)); sp.Update(p, Frame(at, false));
            }
            Tap(new Vector2(10, 10));            // in the scrim, outside the panel
            Assert.True(sp.ScrimDismissed);
            Assert.True(p.IsBlocked(new Vector2(10, 10)));   // scrim reserved its footprint

            Tap(new Vector2(150, 200));          // inside the panel
            Assert.False(sp.ScrimDismissed);
        }

        [Fact]
        public void Scrim_dismiss_ignores_a_drag_that_began_on_the_panel()
        {
            var sp = Make();
            sp.Scrim = new Rect(0, 0, 960, 540);   // full-surface scrim spanning behind the panel (Nullwake's setup)
            var p = new Pointer();
            // Press ON the list, scroll-drag up past the panel's top edge, release in the dimmed scrim above it.
            // Press-origin is inside the panel, release is not: the gesture began on the panel so it must not dismiss.
            p.Update(Frame(new Vector2(150, 200), false)); sp.Update(p, Frame(new Vector2(150, 200), false));
            p.Update(Frame(new Vector2(150, 200), true));  sp.Update(p, Frame(new Vector2(150, 200), true));   // press inside the panel
            p.Update(Frame(new Vector2(150, 50), true));   sp.Update(p, Frame(new Vector2(150, 50), true));    // drag up above the panel (still in the scrim)
            p.Update(Frame(new Vector2(150, 50), false));  sp.Update(p, Frame(new Vector2(150, 50), false));   // release in the scrim above
            Assert.False(sp.ScrimDismissed);
        }

        [Fact]
        public void Scrim_dismiss_still_fires_for_a_drag_that_began_off_the_panel()
        {
            var sp = Make();
            sp.Scrim = new Rect(0, 0, 960, 540);
            var p = new Pointer();
            // Same drag shape, but press-origin AND release are both in the scrim above the panel (never on it):
            // the guard is about the press origin, not about movement, so a genuine off-panel gesture still dismisses.
            p.Update(Frame(new Vector2(150, 40), false)); sp.Update(p, Frame(new Vector2(150, 40), false));
            p.Update(Frame(new Vector2(150, 40), true));  sp.Update(p, Frame(new Vector2(150, 40), true));    // press above the panel
            p.Update(Frame(new Vector2(150, 50), true));  sp.Update(p, Frame(new Vector2(150, 50), true));    // small drag, still off-panel
            p.Update(Frame(new Vector2(150, 50), false)); sp.Update(p, Frame(new Vector2(150, 50), false));   // release in the scrim
            Assert.True(sp.ScrimDismissed);
        }

        // ---- opt-in height glide (10.120.0) ----------------------------------------------------------------

        [Fact]
        public void Height_glide_off_by_default_snaps_every_frame_exactly_as_before()
        {
            var sp = Make();   // HeightGlideSeconds defaults to 0
            var p = new Pointer();
            var pos = new Vector2(150, 200);

            Assert.Equal(300f, sp.EffectiveHeight, 3);   // Box.Height

            sp.Update(p, Frame(pos, false), 0.016f);
            Assert.Equal(300f, sp.EffectiveHeight, 3);

            sp.Bounds = new Rect(Box.X, Box.Y, Box.Width, 500f);
            sp.Update(p, Frame(pos, false), 0.016f);
            Assert.Equal(500f, sp.EffectiveHeight, 3);   // jumped instantly, no glide state involved

            // The legacy no-dt overload must behave identically.
            sp.Bounds = new Rect(Box.X, Box.Y, Box.Width, 220f);
            sp.Update(p, Frame(pos, false));
            Assert.Equal(220f, sp.EffectiveHeight, 3);
        }

        [Fact]
        public void Legacy_update_overload_never_glides_even_when_the_knob_is_set()
        {
            var sp = Make();
            sp.HeightGlideSeconds = 5f;   // opted in, but the caller below never feeds dt
            var p = new Pointer();
            var pos = new Vector2(150, 200);

            sp.Update(p, Frame(pos, false));   // legacy overload: forwards dt = 0, never initializes the glide
            Assert.Equal(300f, sp.EffectiveHeight, 3);

            sp.Bounds = new Rect(Box.X, Box.Y, Box.Width, 600f);
            sp.Update(p, Frame(pos, false));
            Assert.Equal(600f, sp.EffectiveHeight, 3);   // still tracks the target directly - no glide, no state
        }

        [Fact]
        public void Height_glide_converges_toward_a_changed_target_over_dt_steps()
        {
            var sp = Make();
            sp.HeightGlideSeconds = 0.25f;
            var p = new Pointer();
            var pos = new Vector2(150, 200);

            sp.Update(p, Frame(pos, false), 0.016f);   // first update after construction: snaps to the initial target
            Assert.Equal(300f, sp.EffectiveHeight, 3);

            sp.Bounds = new Rect(Box.X, Box.Y, Box.Width, 500f);   // target jumps to 500
            sp.Update(p, Frame(pos, false), 0.05f);
            Assert.True(sp.EffectiveHeight > 300f && sp.EffectiveHeight < 500f);   // moved partway, did not snap

            for (int i = 0; i < 200; i++)
                sp.Update(p, Frame(pos, false), 0.05f);
            Assert.Equal(500f, sp.EffectiveHeight, 1);   // converged (within the 0.5px snap threshold)
        }

        [Fact]
        public void Height_glide_is_frame_rate_independent_for_equal_total_time()
        {
            var pos = new Vector2(150, 200);

            var sp1 = Make();
            sp1.HeightGlideSeconds = 0.3f;
            var p1 = new Pointer();
            sp1.Update(p1, Frame(pos, false), 0.016f);          // establish the baseline at the initial target
            sp1.Bounds = new Rect(Box.X, Box.Y, Box.Width, 600f);
            for (int i = 0; i < 60; i++)
                sp1.Update(p1, Frame(pos, false), 1f / 60f);    // 60 steps of 1/60s = 1s total

            var sp2 = Make();
            sp2.HeightGlideSeconds = 0.3f;
            var p2 = new Pointer();
            sp2.Update(p2, Frame(pos, false), 0.016f);
            sp2.Bounds = new Rect(Box.X, Box.Y, Box.Width, 600f);
            for (int i = 0; i < 12; i++)
                sp2.Update(p2, Frame(pos, false), 1f / 12f);    // 12 steps of 1/12s = 1s total

            Assert.Equal(sp1.EffectiveHeight, sp2.EffectiveHeight, 1);
        }

        [Fact]
        public void Height_glide_snaps_once_within_half_a_pixel_of_target()
        {
            var sp = Make();
            sp.HeightGlideSeconds = 0.05f;   // fast time constant
            var p = new Pointer();
            var pos = new Vector2(150, 200);

            sp.Update(p, Frame(pos, false), 0.016f);   // snap to the initial target
            sp.Bounds = new Rect(Box.X, Box.Y, Box.Width, 320f);   // small 20px jump
            for (int i = 0; i < 50; i++)
                sp.Update(p, Frame(pos, false), 0.05f);   // 2.5s at a 0.05s time constant: residual is negligible

            Assert.Equal(320f, sp.EffectiveHeight, 3);   // exactly snapped, not merely close
        }

        [Fact]
        public void Height_glide_snaps_when_the_panel_becomes_fully_hidden()
        {
            var sp = Make();
            sp.HeightGlideSeconds = 0.3f;
            sp.SlideFromBottom = true;
            var p = new Pointer();
            var pos = new Vector2(150, 200);

            sp.Update(p, Frame(pos, false), 0.016f);   // snap to the initial target (baseline)
            sp.Bounds = new Rect(Box.X, Box.Y, Box.Width, 600f);
            sp.Update(p, Frame(pos, false), 0.016f);   // one short step toward 600 - should not have arrived
            Assert.True(sp.EffectiveHeight < 600f);

            sp.TransitionAlpha = 0f;                    // fully hidden
            sp.Update(p, Frame(pos, false), 0.016f);
            Assert.Equal(600f, sp.EffectiveHeight, 3);   // snapped immediately despite the short dt
        }

        [Fact]
        public void Height_glide_does_not_fight_an_active_drag_resize_and_resumes_after_release()
        {
            var sp = Make();
            sp.HeaderHeight = 30f;
            sp.Resizable = true;
            sp.MinHeight = 100f;
            sp.MaxHeight = 400f;
            sp.HeightGlideSeconds = 5f;   // a slow time constant: a lagging glide would fail this test
            var p = new Pointer();

            p.Update(Frame(new Vector2(150, 115), false)); sp.Update(p, Frame(new Vector2(150, 115), false), 0.016f);
            p.Update(Frame(new Vector2(150, 115), true));  sp.Update(p, Frame(new Vector2(150, 115), true), 0.016f);
            p.Update(Frame(new Vector2(150, 65), true));   sp.Update(p, Frame(new Vector2(150, 65), true), 0.016f);
            Assert.Equal(350f, sp.EffectiveHeight, 3);   // the drag applies directly, no glide lag

            // Release: the glide resumes from wherever the drag left it (no jump back toward a stale target).
            p.Update(Frame(new Vector2(150, 65), false)); sp.Update(p, Frame(new Vector2(150, 65), false), 0.016f);
            Assert.Equal(350f, sp.EffectiveHeight, 3);
        }
    }
}
