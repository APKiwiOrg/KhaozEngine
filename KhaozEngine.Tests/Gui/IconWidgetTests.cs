using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.App;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    public class IconWidgetTests
    {
        static InputState Frame(Vector2 pos, bool down)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Left);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540);
        }

        // Headless: null white is never drawn because the batch is null in Begin(null, ...).
        static GuiSurface Surface() => new(null!, null);

        [Fact]
        public void Icon_WithNoAtlas_IsNoOpAndDoesNotThrow()
        {
            var ui = Surface();
            var p = new Pointer();
            p.Update(Frame(new Vector2(16, 16), false));   // idle inside the icon rect
            p.Update(Frame(new Vector2(16, 16), true));    // press-origin inside the icon rect

            ui.Begin(null, p);          // no IconAtlas set -> Icon is a no-op
            ui.Icon(new Rect(0, 0, 32, 32), Icons.Coin, Vector4.One);
            // Decoration: reserves nothing, so a press inside its rect is not captured.
            Assert.False(ui.PointerCaptured);
        }

        [Fact]
        public void IconButton_ReturnsTrueOnTapInAndReservesRect()
        {
            var ui = Surface();
            var p = new Pointer();
            var rect = new Rect(10, 10, 40, 40);
            var at = new Vector2(30, 30);   // inside rect

            p.Update(Frame(at, false));     // idle inside
            p.Update(Frame(at, true));      // press-origin inside
            p.Update(Frame(at, false));     // release inside -> tap fires

            ui.Begin(null, p);
            bool clicked = ui.IconButton(rect, Icons.Play, GuiStyle.Default);
            Assert.True(clicked);
            Assert.True(ui.PointerCaptured);
        }

        [Fact]
        public void IconButton_ReturnsFalseWhenPressOriginOutsideRect()
        {
            var ui = Surface();
            var p = new Pointer();
            var rect = new Rect(10, 10, 40, 40);
            p.Update(Frame(new Vector2(5, 5), false));     // idle outside
            p.Update(Frame(new Vector2(5, 5), true));      // press-origin OUTSIDE
            p.Update(Frame(new Vector2(30, 30), false));   // release inside
            ui.Begin(null, p);
            Assert.False(ui.IconButton(rect, Icons.Play, GuiStyle.Default));
        }

        [Fact]
        public void StatChip_ReservesItsRect()
        {
            var ui = Surface();
            var p = new Pointer();
            var rect = new Rect(0, 0, 120, 36);
            var at = new Vector2(60, 18);   // inside rect

            p.Update(Frame(at, false));
            p.Update(Frame(at, true));      // pressed, press-origin inside rect

            ui.Begin(null, p);
            ui.StatChip(rect, Icons.Coin, LocalizedText.Raw("Gold"), LocalizedText.Raw("120"), font: null!, GuiStyle.Default);
            Assert.True(ui.PointerCaptured);
        }

        // -- StatChip's "label  value" memoization (FormatStatChipText) --
        // Exercised directly (not through StatChip, which only reaches the formatter when font is non-null -
        // needing a GPU-backed SpriteFont) since the caching/formatting logic itself is font-independent.

        [Fact]
        public void FormatStatChipText_JoinsLabelAndValueWithTwoSpaces()
        {
            var ui = Surface();
            Assert.Equal("Gold  120", ui.FormatStatChipText("Gold", "120"));
        }

        [Fact]
        public void FormatStatChipText_EmptyValue_ReturnsLabelAlone()
        {
            var ui = Surface();
            Assert.Equal("Gold", ui.FormatStatChipText("Gold", ""));
        }

        [Fact]
        public void FormatStatChipText_RepeatedCall_SameKey_ReturnsTheSameCachedInstance()
        {
            var ui = Surface();
            string first = ui.FormatStatChipText("HP", "100/100");
            string second = ui.FormatStatChipText("HP", "100/100");

            Assert.Same(first, second);   // a cache hit returns the memoized string, not a fresh interpolation
        }

        [Fact]
        public void FormatStatChipText_DifferentValue_DoesNotReuseAStaleEntry()
        {
            var ui = Surface();
            string atFull = ui.FormatStatChipText("HP", "100/100");
            string afterDamage = ui.FormatStatChipText("HP", "80/100");
            string backToFull = ui.FormatStatChipText("HP", "100/100");   // e.g. a heal back to full

            Assert.Equal("HP  100/100", atFull);
            Assert.Equal("HP  80/100", afterDamage);
            Assert.Equal(atFull, backToFull);
        }

        [Fact]
        public void FormatStatChipText_DifferentLabel_SameValue_AreDistinctEntries()
        {
            var ui = Surface();
            Assert.Equal("HP  10", ui.FormatStatChipText("HP", "10"));
            Assert.Equal("MP  10", ui.FormatStatChipText("MP", "10"));
        }

        [Fact]
        public void FormatStatChipText_MultipleChipsPerFrame_AllStayIndependentlyCorrect()
        {
            // A HUD typically draws several distinct stat chips per frame on the same surface (HP, MP, Gold),
            // interleaved: a single-slot "last value" cache would thrash on every call here and never hit.
            var ui = Surface();
            for (int frame = 0; frame < 3; frame++)
            {
                Assert.Equal("HP  100/100", ui.FormatStatChipText("HP", "100/100"));
                Assert.Equal("MP  40/40", ui.FormatStatChipText("MP", "40/40"));
                Assert.Equal("Gold  9999", ui.FormatStatChipText("Gold", "9999"));
            }
        }

        [Fact]
        public void FormatStatChipText_PastCapacity_ClearsAndKeepsWorking()
        {
            var ui = Surface();
            for (int i = 0; i < GuiSurface.StatChipTextCacheCapacity + 8; i++)
                ui.FormatStatChipText("Score", i.ToString());

            Assert.True(ui.StatChipTextCacheCount <= GuiSurface.StatChipTextCacheCapacity);
            // Still correct after the cache has been cleared and rebuilt at least once.
            Assert.Equal("Score  41", ui.FormatStatChipText("Score", "41"));
        }
    }
}
