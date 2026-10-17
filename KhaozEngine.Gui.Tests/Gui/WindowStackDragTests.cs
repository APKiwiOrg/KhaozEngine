using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Two overlapping <see cref="WindowDrag"/> windows on a <see cref="WindowStack"/>: a title drag moves only
    /// the window the press began on, and the front close square inside the back title grip closes the front
    /// window.
    /// </summary>
    /// <remarks>
    /// The geometry is the Grimhollow one. The back window's grip runs the whole width of its title less its own
    /// close square, so the front window's close square, at the right end of a narrower title, lies inside it.
    /// </remarks>
    public class WindowStackDragTests
    {
        const int Width = 1280;
        const int Height = 720;
        const float TitleHeight = 26f;

        static readonly Rect BackNatural = new(300f, 150f, 520f, 520f);
        static readonly Rect FrontNatural = new(340f, 150f, 440f, 520f);

        readonly WindowStack _stack = new();
        readonly Pointer _pointer = new();

        // One per test-class instance, never a static, so a gesture cannot leak between facts.
        readonly MouseFrames _mouse = new();

        readonly DragWindow _b = new(BackNatural);
        readonly DragWindow _f = new(FrontNatural);

        public WindowStackDragTests()
        {
            _stack.Add(_b);
            _stack.Add(_f);
        }

        static Vector2 Centre(in Rect rect) => new(rect.X + rect.Width * 0.5f, rect.Y + rect.Height * 0.5f);

        // One frame of a real loop: feed the pointer, update the stack, draw it.
        void Frame(Vector2 at, bool held)
        {
            var buttons = new HashSet<MouseButton>();
            if (held) buttons.Add(MouseButton.Left);
            var (pressed, released) = _mouse.Advance(buttons);
            _pointer.Update(new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                buttons, pressed, at, Vector2.Zero, 0, Width, Height, mouseReleased: released));
            _stack.Update(_pointer, InputState.Empty, 1f / 60f);
            _stack.Draw(null!, _pointer);
        }

        static void AssertAt(in Rect expected, in Rect actual)
        {
            Assert.Equal(expected.X, actual.X, 3);
            Assert.Equal(expected.Y, actual.Y, 3);
            Assert.Equal(expected.Width, actual.Width, 3);
            Assert.Equal(expected.Height, actual.Height, 3);
        }

        static Rect Shifted(in Rect rect, Vector2 by) => new(rect.X + by.X, rect.Y + by.Y, rect.Width, rect.Height);

        [Fact]
        public void Geometry_PutsFrontCloseSquareInsideBackGrip()
        {
            Rect close = _f.CloseSquare;
            Rect grip = _b.Grip;
            Assert.True(grip.Contains(new Vector2(close.X, close.Y)));
            Assert.True(grip.Contains(new Vector2(close.X + close.Width - 0.5f, close.Y + close.Height - 0.5f)));
        }

        [Fact]
        public void TitleDrag_MovesOnlyFrontWindow()
        {
            // A point on the front title that the back grip also contains.
            Vector2 start = Centre(_f.Grip);
            Assert.True(_b.Grip.Contains(start));

            Frame(start, held: false);
            Frame(start, held: true);
            Frame(start + new Vector2(25f, 15f), held: true);
            Frame(start + new Vector2(50f, 30f), held: true);
            Frame(start + new Vector2(50f, 30f), held: false);

            AssertAt(Shifted(FrontNatural, new Vector2(50f, 30f)), _f.Bounds);
            AssertAt(BackNatural, _b.Bounds);
            Assert.Equal(new IStackWindow[] { _b, _f }, _stack.Order);
        }

        [Fact]
        public void FrontCloseSquare_InsideBackGrip_ClosesFront()
        {
            Vector2 close = Centre(_f.CloseSquare);

            Frame(close, held: false);
            Frame(close, held: true);
            Frame(close, held: false);

            Assert.False(_f.IsOpen);
            Assert.True(_b.IsOpen);
            AssertAt(BackNatural, _b.Bounds);
        }

        [Fact]
        public void BackVisibleTitle_DragsBack_AcrossFront()
        {
            // The back title left of the front window.
            Vector2 start = new(BackNatural.X + 20f, BackNatural.Y + TitleHeight * 0.5f);
            Assert.False(_f.Bounds.Contains(start));

            Frame(start, held: false);
            Frame(start, held: true);
            Assert.Equal(new IStackWindow[] { _f, _b }, _stack.Order);

            // Three hops carry the cursor from left of the front window, across it, and past its middle.
            for (int hop = 1; hop <= 3; hop++)
            {
                var moved = new Vector2(100f * hop, 10f * hop);
                Frame(start + moved, held: true);
                AssertAt(Shifted(BackNatural, moved), _b.Bounds);
                AssertAt(FrontNatural, _f.Bounds);
            }

            Frame(start + new Vector2(300f, 30f), held: false);
            AssertAt(Shifted(BackNatural, new Vector2(300f, 30f)), _b.Bounds);
            AssertAt(FrontNatural, _f.Bounds);
            Assert.True(_f.IsOpen);
            Assert.True(_b.IsOpen);
        }

        // A draggable window as a consumer builds one: a natural rect, a close square at the right end of its
        // title, and a grip of the title row less that square. It closes on a tap of the square before its drag
        // updates, then places itself for the next frame.
        sealed class DragWindow : IStackWindow
        {
            readonly Rect _natural;
            readonly WindowDrag _drag = new();

            public DragWindow(Rect natural)
            {
                _natural = natural;
                Bounds = natural;
            }

            public bool IsOpen { get; private set; } = true;
            public Rect Bounds { get; private set; }

            public Rect CloseSquare => new(Bounds.X + Bounds.Width - TitleHeight, Bounds.Y, TitleHeight, TitleHeight);
            public Rect Grip => new(Bounds.X, Bounds.Y, Bounds.Width - TitleHeight, TitleHeight);

            public void Close()
            {
                IsOpen = false;
                _drag.Release();
            }

            public void Update(in WindowUpdate update)
            {
                if (update.Pointer.IsTapIn(CloseSquare)) { Close(); return; }
                _drag.Update(update.Pointer, Grip);
                Bounds = _drag.Place(_natural, new Vector2(Width, Height));
            }

            public void Draw(SpriteBatch batch, Pointer pointer)
            {
            }
        }
    }
}
