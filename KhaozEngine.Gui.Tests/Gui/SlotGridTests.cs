using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class SlotGridTests
    {
        // One per test-class instance (xUnit builds a fresh instance per fact), so the mouse press and
        // release edges derive from this test's own frame sequence and nothing crosses between tests.
        readonly MouseFrames _mouse = new();

        InputState Frame(Vector2 pos, bool leftDown)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            var (edgePressed, edgeReleased) = _mouse.Advance(down);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                down, edgePressed, pos, Vector2.Zero, 0, 960, 540, mouseReleased: edgeReleased);
        }

        // An "up" frame at `at` (windowFocused defaults true) so IsHoveringIn reports the hovered slot.
        Pointer Hovering(Vector2 at)
        {
            var p = new Pointer();
            p.Update(Frame(at, false));
            return p;
        }

        // Press then release at the same point -> a valid press-origin tap (IsTapIn).
        Pointer Tapping(Vector2 at)
        {
            var p = new Pointer();
            p.Update(Frame(at, false));   // up
            p.Update(Frame(at, true));    // press at `at`
            p.Update(Frame(at, false));   // release at `at`
            return p;
        }

        // 5x6 inventory: 30 slots, 5 columns, slot 40, spacing 4, origin (100,100).
        static SlotGrid Grid() => new(new Rect(100, 100, 0, 0), count: 30, columns: 5) { SlotSize = 40f, Spacing = 4f };

        static void AssertRect(float x, float y, float w, float h, Rect r)
        {
            Assert.Equal(x, r.X, 3);
            Assert.Equal(y, r.Y, 3);
            Assert.Equal(w, r.Width, 3);
            Assert.Equal(h, r.Height, 3);
        }

        [Fact]
        public void Rows_is_ceil_count_over_columns()
        {
            Assert.Equal(6, Grid().Rows);
            Assert.Equal(1, new SlotGrid(new Rect(0, 0, 0, 0), 10, 10).Rows);   // 10-slot hotbar
            Assert.Equal(2, new SlotGrid(new Rect(0, 0, 0, 0), 11, 10).Rows);   // partial last row
            Assert.Equal(0, new SlotGrid(new Rect(0, 0, 0, 0), 0, 5).Rows);     // empty
        }

        [Fact]
        public void SlotRect_lays_out_columns_then_rows_with_spacing()
        {
            var g = Grid();
            AssertRect(100, 100, 40, 40, g.SlotRect(0));   // origin
            AssertRect(144, 100, 40, 40, g.SlotRect(1));   // one column over: x += 40 + 4
            AssertRect(100, 144, 40, 40, g.SlotRect(5));   // wraps to row 1, col 0: y += 44
            AssertRect(188, 144, 40, 40, g.SlotRect(7));   // row 1, col 2
        }

        [Fact]
        public void ContentSize_covers_full_columns_and_rows()
        {
            var g = Grid();                        // 5 cols, 6 rows, slot 40, spacing 4
            Assert.Equal(5 * 40 + 4 * 4, g.ContentSize.X, 3);   // 216
            Assert.Equal(6 * 40 + 5 * 4, g.ContentSize.Y, 3);   // 260
        }

        [Fact]
        public void ContentSize_partial_single_row_is_only_count_wide()
        {
            var g = new SlotGrid(new Rect(0, 0, 0, 0), count: 3, columns: 10) { SlotSize = 40f, Spacing = 4f };
            Assert.Equal(3 * 40 + 2 * 4, g.ContentSize.X, 3);   // 128, not the full 10 columns
            Assert.Equal(40f, g.ContentSize.Y, 3);              // one row tall
        }

        [Fact]
        public void SlotAt_finds_the_slot_and_reports_gaps_as_off()
        {
            var g = Grid();
            Assert.Equal(0, g.SlotAt(new Vector2(120, 120)));   // inside slot 0 [100,140)
            Assert.Equal(1, g.SlotAt(new Vector2(160, 120)));   // inside slot 1 [144,184)
            Assert.Equal(-1, g.SlotAt(new Vector2(142, 120)));  // in the 4px gap (140..144)
            Assert.Equal(-1, g.SlotAt(new Vector2(5, 5)));      // off-grid
        }

        [Fact]
        public void Update_reports_the_hovered_slot_from_the_pointer()
        {
            var g = Grid();
            g.Update(Hovering(new Vector2(160, 120)));   // slot 1
            Assert.Equal(1, g.HoveredSlot);
            Assert.Equal(-1, g.PressedSlot);
        }

        [Fact]
        public void Update_hover_in_a_gap_reports_no_slot()
        {
            var g = Grid();
            g.Update(Hovering(new Vector2(142, 120)));   // gap between slot 0 and 1
            Assert.Equal(-1, g.HoveredSlot);
        }

        [Fact]
        public void Update_reports_the_pressed_slot()
        {
            var g = Grid();
            var p = new Pointer();
            p.Update(Frame(new Vector2(160, 120), false));   // up
            p.Update(Frame(new Vector2(160, 120), true));    // press in slot 1
            g.Update(p);
            Assert.Equal(1, g.PressedSlot);
        }

        [Fact]
        public void Tap_fires_onclick_for_the_slot_and_returns_its_index()
        {
            var g = Grid();
            int clicked = -1;
            g.OnSlotClicked = i => clicked = i;
            int ret = g.Update(Tapping(new Vector2(160, 120)));   // slot 1
            Assert.Equal(1, ret);
            Assert.Equal(1, clicked);
        }

        [Fact]
        public void Tap_that_began_in_another_slot_does_not_fire_either_slot()
        {
            var g = Grid();
            int clicked = -1;
            g.OnSlotClicked = i => clicked = i;
            var p = new Pointer();
            p.Update(Frame(new Vector2(120, 120), false));   // up over slot 0
            p.Update(Frame(new Vector2(120, 120), true));    // press in slot 0
            p.Update(Frame(new Vector2(160, 120), false));   // release over slot 1
            int ret = g.Update(p);
            Assert.Equal(-1, ret);        // press-origin invariant: origin in 0, release in 1
            Assert.Equal(-1, clicked);
        }

        [Fact]
        public void Update_blocks_the_footprint_for_click_through()
        {
            var g = Grid();
            var p = Hovering(new Vector2(160, 120));
            g.Update(p);
            Assert.True(p.IsBlocked(new Vector2(120, 120)));    // a slot
            Assert.False(p.IsBlocked(new Vector2(1000, 1000))); // outside the footprint
        }

        [Fact]
        public void SetContent_then_TryGetContent_roundtrips_the_fields()
        {
            var g = Grid();
            var c = new SlotContent(Icons.Coin, Vector4.One, cooldown: 0.5f, count: 3, disabled: true);
            g.SetContent(2, c);

            Assert.Equal(1, g.ContentCount);
            Assert.True(g.TryGetContent(2, out SlotContent got));
            Assert.Equal(Icons.Coin, got.IconId);
            Assert.Equal(0.5f, got.Cooldown, 3);
            Assert.Equal(3, got.Count);
            Assert.True(got.Disabled);
        }

        [Fact]
        public void ClearContent_removes_one_slot_ClearAllContent_removes_all()
        {
            var g = Grid();
            g.SetContent(0, new SlotContent(Icons.Coin));
            g.SetContent(1, new SlotContent(Icons.Heart));
            g.ClearContent(0);
            Assert.False(g.TryGetContent(0, out _));
            Assert.True(g.TryGetContent(1, out _));

            g.ClearAllContent();
            Assert.Equal(0, g.ContentCount);
        }

        [Fact]
        public void Content_survives_a_count_shrink_and_regrow()
        {
            var g = Grid();                 // 30 slots
            g.SetContent(20, new SlotContent(Icons.Gear));
            g.Count = 5;                    // slot 20 is now out of range (would not be drawn)
            Assert.True(g.TryGetContent(20, out _));   // still stored, not lost
            g.Count = 30;                   // back in range
            Assert.True(g.TryGetContent(20, out _));
        }

        [Fact]
        public void SlotContent_clamps_cooldown_to_the_unit_range()
        {
            Assert.Equal(1f, new SlotContent(Icons.Coin, Vector4.One, cooldown: 5f).Cooldown, 3);
            Assert.Equal(0f, new SlotContent(Icons.Coin, Vector4.One, cooldown: -2f).Cooldown, 3);
        }

        [Fact]
        public void SlotContent_single_arg_ctor_defaults_to_white_tint()
        {
            var c = new SlotContent(Icons.Coin);
            Assert.Equal(Vector4.One, c.Tint);
            Assert.Equal(0f, c.Cooldown, 3);
            Assert.Equal(0, c.Count);
            Assert.False(c.Disabled);
        }

        [Fact]
        public void SetContent_negative_index_throws()
        {
            var g = Grid();
            Assert.Throws<System.ArgumentOutOfRangeException>(() => g.SetContent(-1, new SlotContent(Icons.Coin)));
        }

        // -- ResolveCountText (CountFormatter's draw-time decision) --
        // Exercised directly (not through Draw, which only reaches it when font is non-null - needing a
        // GPU-backed SpriteFont and SpriteBatch) since the decision itself is font- and batch-independent, the
        // same pattern as IconWidgetTests.FormatStatChipText_* for GuiSurface.

        [Fact]
        public void ResolveCountText_NullFormatter_DrawsNothingAtZeroOrBelow()
        {
            var g = Grid();
            Assert.Null(g.ResolveCountText(0, new SlotContent(Icons.Coin, Vector4.One, count: 0)));
            Assert.Null(g.ResolveCountText(0, new SlotContent(Icons.Coin, Vector4.One, count: -3)));
        }

        [Fact]
        public void ResolveCountText_NullFormatter_MatchesTodaysInvariantCultureToString()
        {
            var g = Grid();
            var content = new SlotContent(Icons.Coin, Vector4.One, count: 42);
            Assert.Equal(42.ToString(System.Globalization.CultureInfo.InvariantCulture), g.ResolveCountText(0, content));
            Assert.Equal("42", g.ResolveCountText(0, content));
        }

        [Fact]
        public void ResolveCountText_Formatter_ReceivesTheSlotIndexAndContentVerbatim()
        {
            var g = Grid();
            var content = new SlotContent(Icons.Heart, Vector4.One, cooldown: 0.25f, count: 7, disabled: true);
            int seenIndex = -1;
            SlotContent seenContent = default;
            g.CountFormatter = (i, c) => { seenIndex = i; seenContent = c; return "ignored"; };

            g.ResolveCountText(4, content);

            Assert.Equal(4, seenIndex);
            Assert.Equal(Icons.Heart, seenContent.IconId);
            Assert.Equal(7, seenContent.Count);
            Assert.Equal(0.25f, seenContent.Cooldown, 3);
            Assert.True(seenContent.Disabled);
        }

        [Fact]
        public void ResolveCountText_Formatter_ReturnIsDrawnVerbatim()
        {
            var g = Grid();
            g.CountFormatter = (_, c) => $"x{c.Count}";
            Assert.Equal("x5", g.ResolveCountText(0, new SlotContent(Icons.Coin, Vector4.One, count: 5)));
        }

        [Fact]
        public void ResolveCountText_Formatter_NullOrEmptyReturnDrawsNothing()
        {
            var g = Grid();
            g.CountFormatter = (_, _) => null;
            Assert.Null(g.ResolveCountText(0, new SlotContent(Icons.Coin, Vector4.One, count: 5)));

            g.CountFormatter = (_, _) => "";
            Assert.Null(g.ResolveCountText(0, new SlotContent(Icons.Coin, Vector4.One, count: 5)));
        }

        [Fact]
        public void ResolveCountText_Formatter_InvokedEvenWhenCountIsZero()
        {
            var g = Grid();
            bool invoked = false;
            g.CountFormatter = (_, c) => { invoked = true; return c.Count > 0 ? "some" : null; };

            string? result = g.ResolveCountText(0, new SlotContent(Icons.Coin, Vector4.One, count: 0));

            Assert.True(invoked);
            Assert.Null(result);   // the formatter chose null for this call, but it WAS called
        }

        // -- TryResolveIcon (FallbackIconId's draw-time decision) --
        // Same pattern: exercised directly rather than through Draw, which needs a real batch.

        [Fact]
        public void TryResolveIcon_PrimaryIconResolves_FallbackIsNotConsulted()
        {
            var atlas = new IconAtlas();
            var primaryUv = new Vector4(0f, 0f, 0.5f, 0.5f);
            var fallbackUv = new Vector4(0.5f, 0.5f, 1f, 1f);
            atlas.Register("game.sword", null!, primaryUv);
            atlas.Register("game.unknown", null!, fallbackUv);

            var g = Grid();
            g.IconAtlas = atlas;
            g.FallbackIconId = "game.unknown";

            bool ok = g.TryResolveIcon(new SlotContent("game.sword"), out _, out Vector4 uv);
            Assert.True(ok);
            Assert.Equal(primaryUv, uv);
        }

        [Fact]
        public void TryResolveIcon_AtlasMiss_FallsBackToFallbackIconId()
        {
            var atlas = new IconAtlas();
            var fallbackUv = new Vector4(0.5f, 0.5f, 1f, 1f);
            atlas.Register("game.unknown", null!, fallbackUv);   // "game.sword" is never registered: a miss

            var g = Grid();
            g.IconAtlas = atlas;
            g.FallbackIconId = "game.unknown";

            bool ok = g.TryResolveIcon(new SlotContent("game.sword"), out _, out Vector4 uv);
            Assert.True(ok);
            Assert.Equal(fallbackUv, uv);
        }

        [Fact]
        public void TryResolveIcon_NullIconId_NeverFallsBack()
        {
            var atlas = new IconAtlas();
            atlas.Register("game.unknown", null!, new Vector4(0.5f, 0.5f, 1f, 1f));

            var g = Grid();
            g.IconAtlas = atlas;
            g.FallbackIconId = "game.unknown";

            // A null IconId deliberately means "no icon": it must not resolve to the fallback either.
            bool ok = g.TryResolveIcon(new SlotContent(null, Vector4.One), out _, out _);
            Assert.False(ok);
        }

        [Fact]
        public void TryResolveIcon_MissWithNoFallbackSet_StillResolvesNothing()
        {
            var atlas = new IconAtlas();   // "game.sword" is never registered, and FallbackIconId stays null

            var g = Grid();
            g.IconAtlas = atlas;

            bool ok = g.TryResolveIcon(new SlotContent("game.sword"), out _, out _);
            Assert.False(ok);
        }

        [Fact]
        public void TryResolveIcon_FallbackIdItselfMisses_StillResolvesNothing()
        {
            var atlas = new IconAtlas();   // neither "game.sword" nor "game.unknown" is registered

            var g = Grid();
            g.IconAtlas = atlas;
            g.FallbackIconId = "game.unknown";

            bool ok = g.TryResolveIcon(new SlotContent("game.sword"), out _, out _);
            Assert.False(ok);
        }
    }
}
