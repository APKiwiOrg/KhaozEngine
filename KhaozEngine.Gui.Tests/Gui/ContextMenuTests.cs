using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class ContextMenuTests
    {
        // 10px/char, 20px line height (the fake-measurer idiom from TooltipTests).
        sealed class FixedFont : ITextMeasurer
        {
            public float LineHeight => 20f;
            public Vector2 Measure(string text) => new(text.Length * 10f, 20f);
        }

        static readonly FixedFont Font = new();
        static readonly Vector2 View = new(960, 540);
        static readonly ContextMenuMetrics M = ContextMenuMetrics.Default;

        // With the fixed font and the default metrics: the title band is LineHeight(20) + RowPadY*2(8) +
        // TitleGap(5) = 33, and each entry row is LineHeight(20) + RowPadY*2(8) = 28.
        const float TitleBand = 33f;
        const float RowH = 28f;

        // "Attack" is the widest LABEL (60), but the "Use" row carries a right detail, so its total is
        // 30 + DetailGap(16) + 40 = 86 and it is the row that drives the width.
        static ContextMenuEntry[] Two() => new[]
        {
            new ContextMenuEntry("Attack"),
            new ContextMenuEntry("Use", "Rope"),
        };

        [Fact]
        public void Bounds_size_to_widest_row_including_right_detail()
        {
            Rect r = ContextMenu.ComputeBounds(Font, "", Font, Two(), new Vector2(300, 200), View, M);
            // PadX*2(20) + label "Use"(30) + DetailGap(16) + detail "Rope"(40) = 106, wider than the
            // detail-less "Attack" row at 20 + 60 = 80.
            Assert.Equal(106f, r.Width);
        }

        [Fact]
        public void Bounds_top_left_sits_at_the_point()
        {
            var point = new Vector2(300, 200);
            Rect r = ContextMenu.ComputeBounds(Font, "Options", Font, Two(), point, View, M);
            Assert.Equal(point.X, r.X);
            Assert.Equal(point.Y, r.Y);
            // Title band plus two rows. The title band is always present, even with an empty title.
            Assert.Equal(TitleBand + 2f * RowH, r.Height);
        }

        [Fact]
        public void Bounds_clamp_inside_the_right_edge()
        {
            // The menu is 106 wide, so opening at x=950 would run to 1056, well past the viewport.
            Rect r = ContextMenu.ComputeBounds(Font, "", Font, Two(), new Vector2(950, 200), View, M);
            Assert.True(r.Right <= View.X - M.Margin);
            Assert.Equal(View.X - M.Margin, r.Right);
        }

        [Fact]
        public void Bounds_flip_above_when_the_bottom_would_overflow()
        {
            // Height is 89. Opening down from y=500 would reach 589, past the 536 bottom limit, so the menu
            // flips and sits with its bottom on the point instead.
            var point = new Vector2(300, 500);
            Rect r = ContextMenu.ComputeBounds(Font, "", Font, Two(), point, View, M);
            Assert.Equal(point.Y, r.Bottom);
            Assert.Equal(point.Y - (TitleBand + 2f * RowH), r.Y);
        }

        [Fact]
        public void Bounds_size_to_the_title_when_it_is_wider_than_every_row()
        {
            // The title is 22 chars (220), well past the 86 of the widest row, so the title drives the
            // width: 220 + PadX*2(20) = 240.
            Rect r = ContextMenu.ComputeBounds(Font, "Bank of the Wilderness", Font, Two(),
                new Vector2(100, 100), View, M);
            Assert.Equal(240f, r.Width);
        }

        [Fact]
        public void Bounds_clamp_wins_over_the_flip_near_the_bottom_edge()
        {
            // Height is 89. Opening down from y=538 overflows, so the menu flips to y=449 to put its bottom
            // on the point, and the clamp then pulls it back to the last y that fits the margin box,
            // 540 - 89 - 4 = 447. The bottom therefore lands on 536, not on the point's 538.
            var point = new Vector2(300, 538);
            Rect r = ContextMenu.ComputeBounds(Font, "", Font, Two(), point, View, M);
            Assert.Equal(447f, r.Y);
            Assert.Equal(536f, r.Bottom);
            Assert.Equal(View.Y - M.Margin, r.Bottom);
            Assert.NotEqual(point.Y, r.Bottom);
        }

        [Fact]
        public void Bounds_clamp_collapses_a_menu_taller_than_the_space_above_onto_its_anchor()
        {
            // Ten rows over the title band is 313 tall, more than the 296 between the point and the top
            // margin, so neither placement fits. The flip puts y at -13 and the clamp pins it to the
            // margin, which leaves the menu covering its own anchor point.
            var entries = new ContextMenuEntry[10];
            for (int i = 0; i < entries.Length; i++) entries[i] = new ContextMenuEntry("Item");
            var point = new Vector2(100, 300);
            Rect r = ContextMenu.ComputeBounds(Font, "", Font, entries, point, View, M);
            Assert.Equal(M.Margin, r.Y);
            Assert.Equal(M.Margin + TitleBand + 10f * RowH, r.Bottom);
            Assert.True(r.Contains(point));
        }

        [Fact]
        public void Row_rects_stack_below_the_title_band_without_gaps()
        {
            var entries = new[]
            {
                new ContextMenuEntry("One"),
                new ContextMenuEntry("Two"),
                new ContextMenuEntry("Three"),
            };
            Rect b = ContextMenu.ComputeBounds(Font, "Options", Font, entries, new Vector2(100, 100), View, M);
            Rect r0 = ContextMenu.RowBounds(b, Font, Font, 0, M);
            Rect r1 = ContextMenu.RowBounds(b, Font, Font, 1, M);
            Rect r2 = ContextMenu.RowBounds(b, Font, Font, 2, M);

            Assert.Equal(b.Y + TitleBand, r0.Y);
            Assert.Equal(RowH, r0.Height);
            Assert.Equal(r0.Bottom, r1.Y);
            Assert.Equal(r1.Bottom, r2.Y);
            Assert.Equal(b.Bottom, r2.Bottom);   // the rows fill the bounds under the title band exactly
            Assert.Equal(b.X, r0.X);
            Assert.Equal(b.Width, r0.Width);
        }

        [Fact]
        public void Entry_Of_resolves_localized_text_and_a_default_detail_is_empty()
        {
            // default(LocalizedText).Resolve() is the empty string, so the optional detail needs no null guard.
            ContextMenuEntry bare = ContextMenuEntry.Of(LocalizedText.Raw("Attack"));
            Assert.Equal("Attack", bare.Label);
            Assert.Equal("", bare.RightDetail);
            Assert.True(bare.Enabled);
            Assert.Equal(0L, bare.Tag);
            Assert.Null(bare.LabelColor);
            Assert.Null(bare.DetailColor);

            ContextMenuEntry full = ContextMenuEntry.Of(LocalizedText.Raw("Use"), LocalizedText.Raw("Rope"),
                labelColor: Vector4.One, tag: 7, enabled: false);
            Assert.Equal("Use", full.Label);
            Assert.Equal("Rope", full.RightDetail);
            Assert.Equal(7L, full.Tag);
            Assert.False(full.Enabled);
            Assert.True(full.LabelColor.HasValue);
            Assert.Equal(Vector4.One, full.LabelColor.Value);
        }

        [Fact]
        public void Segmented_label_width_is_the_sum_of_caller_ordered_segments()
        {
            var entry = ContextMenuEntry.Segmented(new[]
            {
                new LabelSegment(LocalizedText.Raw("Goblin "), new Vector4(0f, 1f, 0f, 1f)),
                new LabelSegment(LocalizedText.Raw("Attack"), new Vector4(1f, 0f, 0f, 1f)),
            }, tag: 42);

            Rect bounds = ContextMenu.ComputeBounds(Font, "", Font, new[] { entry },
                new Vector2(100, 100), View, M);
            ContextMenu.LabelRun[] runs = ContextMenu.LayoutLabel(entry, Font, 100f, Vector4.One, Vector4.Zero);

            Assert.Equal(130f + M.PadX * 2f, bounds.Width);
            Assert.Equal("Goblin ", runs[0].Text);
            Assert.Equal(100f, runs[0].X);
            Assert.Equal(new Vector4(0f, 1f, 0f, 1f), runs[0].Color);
            Assert.Equal("Attack", runs[1].Text);
            Assert.Equal(170f, runs[1].X);
            Assert.Equal(new Vector4(1f, 0f, 0f, 1f), runs[1].Color);
            Assert.Equal(42L, entry.Tag);
        }

        [Fact]
        public void Segmented_label_matches_legacy_layout_for_the_same_resolved_text()
        {
            var legacy = new ContextMenuEntry("Attack Goblin");
            var segmented = ContextMenuEntry.Segmented(new[]
            {
                new LabelSegment(LocalizedText.Raw("Attack ")),
                new LabelSegment(LocalizedText.Raw("Goblin")),
            });

            Rect legacyBounds = ContextMenu.ComputeBounds(Font, "", Font, new[] { legacy },
                new Vector2(100, 100), View, M);
            Rect segmentedBounds = ContextMenu.ComputeBounds(Font, "", Font, new[] { segmented },
                new Vector2(100, 100), View, M);

            Assert.Equal(legacyBounds, segmentedBounds);
        }

        // ---- interaction ----------------------------------------------------
        //
        // The interaction fixture is a three-row menu opened at (300,200) in the 960x540 viewport. With the
        // fixed font the widest row is "Use" + DetailGap + "Rope" (86), so the menu is 106 wide and
        // TitleBand(33) + 3 * RowH(28) = 117 tall, and nothing flips or clamps at that point. Rows therefore
        // run 233..261, 261..289 and 289..317, and the third row is disabled.

        static readonly Vector2 Point = new(300, 200);
        static readonly Vector2 Row0Pt = new(350, 247);
        static readonly Vector2 Row1Pt = new(350, 275);
        static readonly Vector2 Row2Pt = new(350, 303);   // the disabled row
        static readonly Vector2 TitlePt = new(350, 210);  // inside the menu, on the title band
        static readonly Vector2 Outside = new(700, 400);

        static ContextMenuEntry[] Three() => new[]
        {
            new ContextMenuEntry("Attack", Tag: 11),
            new ContextMenuEntry("Use", "Rope", Tag: 22),
            new ContextMenuEntry("Bury", Tag: 33, Enabled: false),
        };

        // One per test-class instance (xUnit builds a fresh instance per fact), so the press and release edges
        // come from this test's own frame sequence, per the MouseFrames contract.
        readonly MouseFrames _mouse = new();

        InputState Frame(Vector2 pos, bool down)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Left);
            var (edgePressed, edgeReleased) = _mouse.Advance(b);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, edgePressed, pos, Vector2.Zero, 0, 960, 540, mouseReleased: edgeReleased);
        }

        static ContextMenu OpenMenu(ContextMenuEntry[] entries, Vector2 point)
        {
            var menu = new ContextMenu(Font, Font) { Viewport = View };
            menu.Open(LocalizedText.Raw("Options"), entries, point);
            return menu;
        }

        void Tap(ContextMenu menu, Pointer p, Vector2 at)
        {
            p.Update(Frame(at, false)); menu.Update(p);
            p.Update(Frame(at, true)); menu.Update(p);
            p.Update(Frame(at, false)); menu.Update(p);
        }

        void Idle(ContextMenu menu, Pointer p, Vector2 at)
        {
            p.Update(Frame(at, false));
            menu.Update(p);
        }

        // The right-button twin of Frame, for the gesture the control deliberately ignores.
        InputState RightFrame(Vector2 pos, bool down)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Right);
            var (edgePressed, edgeReleased) = _mouse.Advance(b);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, edgePressed, pos, Vector2.Zero, 0, 960, 540, mouseReleased: edgeReleased);
        }

        [Fact]
        public void Draw_throws_while_the_viewport_is_unset()
        {
            // The guard runs before batch or white is touched, so nulls never reach the draw calls.
            var menu = new ContextMenu(Font, Font);
            menu.Open(LocalizedText.Raw("Options"), Three(), Point);

            Assert.Equal(Vector2.Zero, menu.Viewport);
            var ex = Assert.Throws<InvalidOperationException>(() => menu.Draw(null!, null!));
            Assert.Contains("Viewport", ex.Message);

            // A CLOSED menu returns before every guard, so the same call is a no-op.
            menu.Close();
            menu.Draw(null!, null!);
        }

        [Fact]
        public void Draw_throws_on_a_measure_only_menu()
        {
            // Viewport is assigned, so this is the font guard rather than the viewport one.
            var menu = new ContextMenu(Font, Font) { Viewport = View };
            menu.Open(LocalizedText.Raw("Options"), Three(), Point);

            var ex = Assert.Throws<InvalidOperationException>(() => menu.Draw(null!, null!));
            Assert.Contains("measure-only", ex.Message);
        }

        [Fact]
        public void A_right_press_and_release_outside_leaves_the_menu_open()
        {
            // Dismissal is a LEFT release outside or menu-cancel. The right button does nothing to an open menu,
            // which is why a caller wanting right-click-to-reopen has to close and reopen it itself.
            ContextMenu menu = OpenMenu(Three(), Point);
            var p = new Pointer();

            p.Update(RightFrame(Outside, false)); menu.Update(p);
            p.Update(RightFrame(Outside, true)); menu.Update(p);
            p.Update(RightFrame(Outside, false));
            Assert.True(p.IsRightJustReleased);   // the right gesture really did complete outside the menu
            menu.Update(p);

            Assert.True(menu.IsOpen);
            Assert.False(menu.WasDismissed);
            Assert.Null(menu.DismissPress);

            // And the left release outside still dismisses, so the menu was reachable all along.
            Tap(menu, p, Outside);
            Assert.False(menu.IsOpen);
            Assert.True(menu.WasDismissed);
        }

        [Fact]
        public void Tap_on_a_row_selects_its_tag_and_closes()
        {
            ContextMenu menu = OpenMenu(Three(), Point);
            var p = new Pointer();

            Tap(menu, p, Row1Pt);

            Assert.True(menu.WasSelected);
            Assert.Equal(22L, menu.SelectedTag);
            Assert.Equal(1, menu.SelectedIndex);
            Assert.False(menu.IsOpen);
            Assert.False(menu.WasDismissed);
        }

        [Fact]
        public void Tap_on_a_segmented_row_selects_its_tag_unchanged()
        {
            var entry = ContextMenuEntry.Segmented(new[]
            {
                new LabelSegment(LocalizedText.Raw("Attack ")),
                new LabelSegment(LocalizedText.Raw("Goblin")),
            }, tag: 77);
            ContextMenu menu = OpenMenu(new[] { entry }, Point);
            var p = new Pointer();

            Tap(menu, p, Row0Pt);

            Assert.True(menu.WasSelected);
            Assert.Equal(77L, menu.SelectedTag);
            Assert.Equal(0, menu.SelectedIndex);
        }

        [Fact]
        public void Tap_on_a_disabled_row_selects_nothing_and_stays_open()
        {
            ContextMenu menu = OpenMenu(Three(), Point);
            var p = new Pointer();

            Tap(menu, p, Row2Pt);

            Assert.False(menu.WasSelected);
            Assert.Equal(-1, menu.SelectedIndex);
            Assert.True(menu.IsOpen);       // the tap landed inside the menu, so it is not a dismissal either
            Assert.False(menu.WasDismissed);

            // A tap on the title band is inert the same way: inside the menu, on no row.
            Tap(menu, p, TitlePt);
            Assert.False(menu.WasSelected);
            Assert.True(menu.IsOpen);
        }

        [Fact]
        public void Release_outside_dismisses_and_reports_the_press_position()
        {
            ContextMenu menu = OpenMenu(Three(), Point);
            var p = new Pointer();
            var pressAt = new Vector2(700, 400);
            var releaseAt = new Vector2(800, 450);

            // One idle frame first, so the press below is a FRESH gesture rather than one already in flight on
            // the opening frame. A gesture live on that frame is the opening gesture and can never dismiss.
            p.Update(Frame(pressAt, false)); menu.Update(p);

            // Press outside, drag, release somewhere else outside. DismissPress is documented as the RELEASE
            // position, which is the one IsReleasedOutside keys on, so a drag that starts elsewhere still
            // reopens under the cursor rather than at a stale press origin.
            p.Update(Frame(pressAt, true)); menu.Update(p);
            p.Update(Frame(releaseAt, true)); menu.Update(p);
            p.Update(Frame(releaseAt, false)); menu.Update(p);

            Assert.False(menu.IsOpen);
            Assert.True(menu.WasDismissed);
            Assert.False(menu.WasSelected);
            Assert.Equal(releaseAt, menu.DismissPress);
            Assert.NotEqual(pressAt, menu.DismissPress!.Value);
            Assert.Equal(pressAt, p.PressOrigin);   // the press origin really was the other point
        }

        [Fact]
        public void Menu_cancel_dismisses_via_the_input_manager_overload()
        {
            ContextMenu menu = OpenMenu(Three(), Point);
            var input = new InputManager();

            input.Update(OverlayTestInput.KeyFrame(Key.F1));
            Assert.False(menu.Update(input));
            Assert.True(menu.IsOpen);           // an unrelated key is not a cancel

            input.Update(OverlayTestInput.KeyFrame(Key.Escape));
            Assert.False(menu.Update(input));

            Assert.False(menu.IsOpen);
            Assert.True(menu.WasDismissed);
            Assert.False(menu.WasSelected);
            Assert.Null(menu.DismissPress);     // no outside press to reopen from
        }

        [Fact]
        public void Hover_index_tracks_the_pointer_and_skips_disabled_rows()
        {
            ContextMenu menu = OpenMenu(Three(), Point);
            var p = new Pointer();

            Idle(menu, p, Row0Pt);
            Assert.Equal(0, menu.HoverIndex);

            Idle(menu, p, Row1Pt);
            Assert.Equal(1, menu.HoverIndex);

            Idle(menu, p, Row2Pt);
            Assert.Equal(-1, menu.HoverIndex);   // disabled rows never highlight

            Idle(menu, p, TitlePt);
            Assert.Equal(-1, menu.HoverIndex);

            Idle(menu, p, Outside);
            Assert.Equal(-1, menu.HoverIndex);
        }

        [Fact]
        public void Reopen_while_open_replaces_entries_and_point()
        {
            ContextMenu menu = OpenMenu(Three(), Point);
            var p = new Pointer();
            Idle(menu, p, Row0Pt);
            Assert.Equal(0, menu.HoverIndex);

            // One row, 70 wide (the 4-char title drives nothing here), 33 + 28 = 61 tall at (100,100), so its
            // only row runs 133..161.
            menu.Open(LocalizedText.Raw("Bank"), new[] { new ContextMenuEntry("Close", Tag: 99) }, new Vector2(100, 100));
            Assert.True(menu.IsOpen);

            Idle(menu, p, Row0Pt);
            Assert.Equal(-1, menu.HoverIndex);   // the old point's rows are gone

            var newRow0 = new Vector2(135, 147);
            Idle(menu, p, newRow0);
            Assert.Equal(0, menu.HoverIndex);

            Tap(menu, p, newRow0);
            Assert.True(menu.WasSelected);
            Assert.Equal(99L, menu.SelectedTag);
            Assert.Equal(0, menu.SelectedIndex);
        }

        [Fact]
        public void Open_frame_flags_are_clear_and_selection_flags_last_one_frame()
        {
            ContextMenu menu = OpenMenu(Three(), Point);
            var p = new Pointer();

            Assert.False(menu.WasSelected);
            Assert.False(menu.WasDismissed);
            Assert.Null(menu.DismissPress);
            Assert.Equal(-1, menu.SelectedIndex);
            Assert.Equal(0L, menu.SelectedTag);
            Assert.Equal(-1, menu.HoverIndex);

            Tap(menu, p, Row0Pt);
            Assert.True(menu.WasSelected);
            Assert.Equal(0, menu.SelectedIndex);
            Assert.Equal(11L, menu.SelectedTag);

            // The next Update clears them, closed menu included, so a caller reads them on the frame Update
            // returned true and never off a stale latch.
            Idle(menu, p, Row0Pt);
            Assert.False(menu.WasSelected);
            Assert.Equal(-1, menu.SelectedIndex);
            Assert.Equal(0L, menu.SelectedTag);
            Assert.False(menu.WasDismissed);

            // Same for the dismissal flags.
            menu.Open(LocalizedText.Raw("Options"), Three(), Point);
            Tap(menu, p, Outside);
            Assert.True(menu.WasDismissed);
            Assert.Equal(Outside, menu.DismissPress);
            Idle(menu, p, Outside);
            Assert.False(menu.WasDismissed);
            Assert.Null(menu.DismissPress);
        }

        [Fact]
        public void Update_reserves_the_menu_bounds_against_click_through()
        {
            ContextMenu menu = OpenMenu(Three(), Point);
            var p = new Pointer();

            Idle(menu, p, Outside);
            Assert.True(p.IsBlocked(Row1Pt));    // the world beneath cannot be clicked through the menu
            Assert.True(p.IsBlocked(TitlePt));   // the title band reserves too, it is part of the menu
            Assert.False(p.IsBlocked(Outside));

            // Reservations are per-frame and stop with the menu.
            menu.Close();
            Idle(menu, p, Outside);
            Assert.False(p.IsBlocked(Row1Pt));
        }

        [Fact]
        public void The_reservation_holds_on_the_selection_and_dismissal_frames()
        {
            ContextMenu menu = OpenMenu(Three(), Point);
            var p = new Pointer();

            // The selection frame returns early, before the dismissal check, so the reservation has to be in
            // hand ahead of the selection loop. Move it below and the world beneath acts on the very click
            // that picked the row.
            Tap(menu, p, Row0Pt);
            Assert.True(menu.WasSelected);
            Assert.True(p.IsBlocked(Row0Pt));

            // Same on the frame the menu closes itself: the click that dismissed it is not the world's to act on.
            menu.Open(LocalizedText.Raw("Options"), Three(), Point);
            Tap(menu, p, Outside);
            Assert.True(menu.WasDismissed);
            Assert.True(p.IsBlocked(Row0Pt));
        }

        [Fact]
        public void A_menu_opened_on_a_left_release_survives_its_own_opening_gesture()
        {
            // A caller that opens on a left RELEASE hands the first Update a frame that already carries the
            // release edge. Three rows are 117 tall, so at (300,538) the flip puts the menu at 421 and the
            // clamp pulls it to 419..536: the release at 538 lands OUTSIDE the menu it just opened.
            var at = new Vector2(300, 538);
            var p = new Pointer();
            var menu = new ContextMenu(Font, Font) { Viewport = View };

            p.Update(Frame(at, false));
            p.Update(Frame(at, true));
            p.Update(Frame(at, false));   // the release edge the caller opens on
            menu.Open(LocalizedText.Raw("Options"), Three(), at);

            Rect bounds = ContextMenu.ComputeBounds(Font, "Options", Font, Three(), at, View, M);
            Assert.Equal(419f, bounds.Y);
            Assert.True(p.IsReleasedOutside(bounds));   // the dismissal case really is live this frame

            menu.Update(p);
            Assert.True(menu.IsOpen);           // the opening gesture cannot dismiss the menu it opened
            Assert.False(menu.WasDismissed);
            Assert.Null(menu.DismissPress);

            // The latch holds until a FRESH press arrives, which is the whole of the opening gesture here: the
            // release already happened, so nothing but a new press can clear it. That next gesture dismisses.
            Tap(menu, p, Outside);
            Assert.False(menu.IsOpen);
            Assert.True(menu.WasDismissed);
            Assert.Equal(Outside, menu.DismissPress);
        }

        [Fact]
        public void A_menu_opened_on_a_left_press_survives_that_gesture_s_release_a_frame_later()
        {
            // The press-open twin of the case above, and the one a single-FRAME latch cannot cover: the caller
            // opens on the PRESS edge, so the release lands a frame later, still the same gesture. Three rows
            // are 117 tall, so at (300,538) the flip puts the menu at 421 and the clamp pulls it to 419..536.
            // The release at 538 therefore lands OUTSIDE the menu that press just opened.
            var at = new Vector2(300, 538);
            var p = new Pointer();
            var menu = new ContextMenu(Font, Font) { Viewport = View };

            p.Update(Frame(at, false));
            p.Update(Frame(at, true));    // the press edge the caller opens on
            menu.Open(LocalizedText.Raw("Options"), Three(), at);
            menu.Update(p);
            Assert.True(menu.IsOpen);

            Rect bounds = ContextMenu.ComputeBounds(Font, "Options", Font, Three(), at, View, M);
            Assert.Equal(419f, bounds.Y);

            p.Update(Frame(at, false));                 // the SAME gesture's release, one frame after the open
            Assert.True(p.IsReleasedOutside(bounds));   // the dismissal case really is live this frame
            menu.Update(p);

            Assert.True(menu.IsOpen);                   // the opening gesture cannot dismiss the menu it opened
            Assert.False(menu.WasDismissed);
            Assert.Null(menu.DismissPress);

            // A fresh press outside, on a frame the menu already existed for, still dismisses.
            Tap(menu, p, Outside);
            Assert.False(menu.IsOpen);
            Assert.True(menu.WasDismissed);
            Assert.Equal(Outside, menu.DismissPress);
        }

        [Fact]
        public void A_menu_that_clamps_over_the_cursor_does_not_select_on_its_opening_gesture()
        {
            // Twenty rows over the title band is 593 tall, taller than the whole viewport, so neither placement
            // fits and the clamp pins the menu to y 4, covering its own anchor. The caller opens on the release
            // edge, so the first Update sees a tap whose press-origin AND release both sit on row 9. That press
            // began before the menu existed, so it can never be a deliberate row press.
            var entries = new ContextMenuEntry[20];
            for (int i = 0; i < entries.Length; i++) entries[i] = new ContextMenuEntry("Item", Tag: 100 + i);
            var at = new Vector2(500, 300);
            var p = new Pointer();
            var menu = new ContextMenu(Font, Font) { Viewport = View };

            p.Update(Frame(at, false));
            p.Update(Frame(at, true));
            p.Update(Frame(at, false));   // the release edge the caller opens on
            menu.Open(LocalizedText.Raw("Options"), entries, at);

            Rect bounds = ContextMenu.ComputeBounds(Font, "Options", Font, entries, at, View, M);
            Assert.Equal(M.Margin, bounds.Y);
            Rect row9 = ContextMenu.RowBounds(bounds, Font, Font, 9, M);
            Assert.True(row9.Contains(at));   // the clamped menu really did drop a row under the cursor
            Assert.True(p.IsTapIn(row9));     // and the selection case really is live this frame

            menu.Update(p);
            Assert.False(menu.WasSelected);
            Assert.Equal(-1, menu.SelectedIndex);
            Assert.Equal(0L, menu.SelectedTag);
            Assert.True(menu.IsOpen);

            // Once the latch clears on a fresh press, a tap on that same row selects normally.
            Tap(menu, p, at);
            Assert.True(menu.WasSelected);
            Assert.Equal(9, menu.SelectedIndex);
            Assert.Equal(109L, menu.SelectedTag);
            Assert.False(menu.IsOpen);
        }
    }
}
