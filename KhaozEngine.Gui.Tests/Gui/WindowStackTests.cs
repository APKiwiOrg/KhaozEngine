using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// <see cref="WindowStack"/> over rectangle windows: bands, raise on open and press, top to bottom updates
    /// with the covered area occluded, bottom to top draws, capture, no-input frames, Escape and the occlusion
    /// left for the layers below.
    /// </summary>
    public class WindowStackTests
    {
        const int Width = 1280;
        const int Height = 720;

        // B and F overlap in (200..500, 150..400).
        static readonly Rect BackBounds = new(100f, 100f, 400f, 300f);
        static readonly Rect FrontBounds = new(200f, 150f, 400f, 300f);

        static readonly Vector2 Overlap = new(300f, 200f);
        static readonly Vector2 BackOnly = new(150f, 120f);
        static readonly Vector2 FrontOnly = new(550f, 420f);
        static readonly Vector2 Outside = new(900f, 650f);

        readonly List<string> _log = new();
        readonly WindowStack _stack = new();
        readonly Pointer _pointer = new();

        // One per test-class instance, never a static, so a gesture cannot leak between facts.
        readonly MouseFrames _mouse = new();

        readonly FakeStackWindow _b;
        readonly FakeStackWindow _f;

        public WindowStackTests()
        {
            _b = new FakeStackWindow("B", BackBounds, _log);
            _f = new FakeStackWindow("F", FrontBounds, _log);
        }

        void AddBoth()
        {
            _stack.Add(_b);
            _stack.Add(_f);
        }

        // One frame: feed the pointer, then update the stack against it.
        void Frame(Vector2 at, bool held = false, bool rightHeld = false, bool receivesInput = true)
        {
            var buttons = new HashSet<MouseButton>();
            if (held) buttons.Add(MouseButton.Left);
            if (rightHeld) buttons.Add(MouseButton.Right);
            var (pressed, released) = _mouse.Advance(buttons);
            _pointer.Update(new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                buttons, pressed, at, Vector2.Zero, 0, Width, Height, mouseReleased: released));
            _stack.Update(_pointer, InputState.Empty, 1f / 60f, receivesInput);
        }

        // A press and release of one button that both land inside one frame.
        void TapFrame(Vector2 at, MouseButton button)
        {
            _mouse.Advance(new HashSet<MouseButton>());
            _pointer.Update(new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton> { button },
                at, Vector2.Zero, 0, Width, Height, mouseReleased: new HashSet<MouseButton> { button }));
            _stack.Update(_pointer, InputState.Empty, 1f / 60f);
        }

        void Draw() => _stack.Draw(null!, _pointer);

        void AssertOrder(params IStackWindow[] expected) => Assert.Equal(expected, _stack.Order);

        [Fact]
        public void Add_PlacesAtFrontOfBand()
        {
            AddBoth();
            AssertOrder(_b, _f);
        }

        [Fact]
        public void Update_RunsTopToBottom_DrawRunsBottomToTop()
        {
            AddBoth();
            Frame(Outside);
            Draw();
            Assert.Equal(new[] { "update:F", "update:B", "draw:B", "draw:F" }, _log);
        }

        [Fact]
        public void CoveredArea_IsDeadForBackWindow()
        {
            AddBoth();
            Frame(Overlap);
            Assert.True(_f.Hover);
            Assert.False(_b.Hover);

            Frame(BackOnly);
            Assert.True(_b.Hover);
            Assert.False(_f.Hover);

            // A tap in the overlap is the front window's alone.
            Frame(Overlap, held: true);
            Frame(Overlap);
            Assert.True(_f.Tap);
            Assert.False(_b.Tap);

            // The uncovered part of the back window takes a full tap.
            Frame(BackOnly, held: true);
            Frame(BackOnly);
            Assert.True(_b.Tap);
            Assert.False(_f.Tap);
        }

        [Fact]
        public void Press_RaisesFrontMostWindowUnderOrigin()
        {
            AddBoth();
            Frame(BackOnly);
            _log.Clear();

            Frame(BackOnly, held: true);
            AssertOrder(_f, _b);
            Assert.Equal("update:B", _log[0]);
        }

        [Fact]
        public void Press_InOverlap_KeepsFrontWindow()
        {
            AddBoth();
            Frame(Overlap);
            Frame(Overlap, held: true);
            AssertOrder(_b, _f);
        }

        [Fact]
        public void RightPress_Raises()
        {
            AddBoth();
            Frame(BackOnly);
            Frame(BackOnly, rightHeld: true);
            AssertOrder(_f, _b);
            Frame(BackOnly);

            // A right press and release inside one frame is still a fresh right press.
            TapFrame(FrontOnly, MouseButton.Right);
            AssertOrder(_b, _f);
        }

        [Fact]
        public void Open_RaisesToFront()
        {
            _b.IsOpen = false;
            AddBoth();
            Frame(Outside);
            AssertOrder(_b, _f);

            _b.IsOpen = true;
            Frame(Outside);
            AssertOrder(_f, _b);
        }

        [Fact]
        public void Open_DuringHeldPress_WaitsForRelease()
        {
            _b.IsOpen = false;
            AddBoth();
            var title = new Vector2(400f, 160f);
            Frame(title);
            Frame(title, held: true);
            AssertOrder(_b, _f);

            _b.IsOpen = true;
            Frame(title + new Vector2(30f, 20f), held: true);
            AssertOrder(_b, _f);
            Draw();
            AssertOrder(_b, _f);
            Frame(title + new Vector2(60f, 40f), held: true);
            AssertOrder(_b, _f);

            Frame(title + new Vector2(60f, 40f));
            AssertOrder(_f, _b);
        }

        [Fact]
        public void Open_ByAnotherWindowDuringUpdate_RaisedBeforeDraw()
        {
            var t = new FakeStackWindow("T", new Rect(0f, 0f, 50f, 50f), _log, open: false);
            _stack.Add(t);
            AddBoth();
            _f.OnUpdate = _ => t.IsOpen = true;

            Frame(Outside);
            Draw();
            Assert.Equal("draw:T", _log[^1]);
            AssertOrder(_b, _f, t);
        }

        [Fact]
        public void Bands_DockedNeverAboveFloating_TopmostAlwaysAbove()
        {
            var x = new FakeStackWindow("X", new Rect(450f, 120f, 100f, 100f), _log);
            var d = new FakeStackWindow("D", new Rect(0f, 500f, 1280f, 220f), _log);
            _stack.Add(x, WindowBand.Topmost);
            _stack.Add(d, WindowBand.Docked);
            AddBoth();
            AssertOrder(d, _b, _f, x);

            var dockOnly = new Vector2(100f, 600f);
            Frame(dockOnly);
            Frame(dockOnly, held: true);
            Frame(dockOnly);
            AssertOrder(d, _b, _f, x);

            Frame(BackOnly);
            Frame(BackOnly, held: true);
            Frame(BackOnly);
            AssertOrder(d, _f, _b, x);
        }

        [Fact]
        public void ClosedDuringUpdate_OccludesForThatFrameAndDoesNotDraw()
        {
            AddBoth();
            _f.OnUpdate = u =>
            {
                if (u.Pointer.IsTapIn(_f.Bounds)) _f.Close();
            };
            Frame(Overlap);
            Frame(Overlap, held: true);
            _log.Clear();

            // The closing frame: the player saw F, so the tap that closed it does not reach B behind it.
            Frame(Overlap);
            Assert.False(_f.IsOpen);
            Assert.False(_b.Tap);
            Assert.False(_b.Hover);
            Assert.True(_pointer.IsOccluded(FrontOnly));
            Assert.True(_pointer.IsBlocked(FrontOnly));
            Draw();
            Assert.DoesNotContain("draw:F", _log);
            Assert.Contains("draw:B", _log);

            // From the next frame F is skipped as closed.
            _log.Clear();
            Frame(Overlap);
            Assert.True(_b.Hover);
            Assert.DoesNotContain("update:F", _log);
            Assert.False(_pointer.IsOccluded(FrontOnly));
        }

        [Fact]
        public void RightPress_WhileLeftHeld_RaisesNothingAndDragKeepsItsWindow()
        {
            AddBoth();
            var title = new Vector2(400f, 160f);
            Frame(title);
            Frame(title, held: true);
            Assert.True(_f.Dragging);

            Frame(title + new Vector2(-40f, 0f), held: true);
            Assert.True(_f.Dragging);

            // A right press on B's visible area while the left drag is held.
            Frame(BackOnly, held: true, rightHeld: true);
            AssertOrder(_b, _f);
            Assert.True(_f.Dragging);
            Frame(BackOnly, held: true, rightHeld: true);
            AssertOrder(_b, _f);
            Assert.True(_f.Dragging);
            Frame(BackOnly, held: true);
            AssertOrder(_b, _f);
            Assert.True(_f.Dragging);

            Frame(BackOnly);
            AssertOrder(_b, _f);
            Assert.False(_f.Dragging);
        }

        [Fact]
        public void LeftPress_WhileRightHeld_RaisesNothing()
        {
            AddBoth();
            Frame(FrontOnly);
            Frame(FrontOnly, rightHeld: true);
            AssertOrder(_b, _f);

            Frame(BackOnly, held: true, rightHeld: true);
            AssertOrder(_b, _f);
            Frame(BackOnly, rightHeld: true);
            Frame(BackOnly);
            AssertOrder(_b, _f);
        }

        [Fact]
        public void ReceivesInputFalse_PressRaisesNothing()
        {
            AddBoth();
            Frame(BackOnly);
            Frame(BackOnly, held: true, receivesInput: false);
            AssertOrder(_b, _f);
            Frame(BackOnly, receivesInput: false);

            Frame(BackOnly, rightHeld: true, receivesInput: false);
            AssertOrder(_b, _f);
            Frame(BackOnly, receivesInput: false);

            // The same press with input on does raise, so the facts above are not vacuous.
            Frame(BackOnly, held: true);
            AssertOrder(_f, _b);
        }

        [Fact]
        public void Capture_UpdatesCaptorFirstWithNothingOccluded_OccludesRest()
        {
            AddBoth();
            _b.CapturesPointer = true;
            Frame(Overlap);
            Assert.Equal("update:B", _log[0]);
            Assert.Equal("update:F", _log[1]);
            Assert.True(_b.Hover);
            Assert.False(_f.Hover);

            Frame(FrontOnly);
            Assert.False(_f.Hover);

            // A press on the captor's uncovered part would raise it without capture.
            Frame(BackOnly);
            Frame(BackOnly, held: true);
            AssertOrder(_b, _f);
        }

        [Fact]
        public void ReceivesInputFalse_OccludesEverything()
        {
            AddBoth();
            Frame(Overlap, receivesInput: false);
            Assert.Equal(1, _b.UpdateCount);
            Assert.Equal(1, _f.UpdateCount);
            Assert.False(_b.ReceivesInput);
            Assert.False(_f.ReceivesInput);
            Assert.False(_b.Hover);
            Assert.False(_f.Hover);

            Frame(BackOnly, receivesInput: false);
            Assert.False(_b.Hover);

            Frame(BackOnly);
            Assert.True(_b.ReceivesInput);
            Assert.True(_b.Hover);
        }

        [Fact]
        public void IsFront_IsTopOpenWindowOfBand()
        {
            var d = new FakeStackWindow("D", new Rect(0f, 500f, 1280f, 220f), _log);
            _stack.Add(d, WindowBand.Docked);
            AddBoth();
            Frame(Outside);
            Assert.True(_f.IsFront);
            Assert.False(_b.IsFront);
            Assert.True(d.IsFront);

            _f.IsOpen = false;
            Frame(Outside);
            Assert.True(_b.IsFront);
        }

        [Fact]
        public void CloseFront_RespectsBandsAndClosesOnEscape()
        {
            var x = new FakeStackWindow("X", new Rect(450f, 120f, 100f, 100f), _log);
            var d = new FakeStackWindow("D", new Rect(0f, 500f, 1280f, 220f), _log);
            _stack.Add(d, WindowBand.Docked);
            _stack.Add(x, WindowBand.Topmost);
            AddBoth();
            _f.ClosesOnEscape = false;

            Assert.True(_stack.CloseFront());
            Assert.Equal(1, x.CloseCalls);

            Assert.True(_stack.CloseFront());
            Assert.Equal(0, _f.CloseCalls);
            Assert.Equal(1, _b.CloseCalls);

            Assert.True(_stack.CloseFront());
            Assert.Equal(1, d.CloseCalls);

            Assert.False(_stack.CloseFront());
            Assert.True(_f.IsOpen);
            Assert.Equal(1, x.CloseCalls);
            Assert.Equal(1, _b.CloseCalls);
            Assert.Equal(1, d.CloseCalls);
        }

        [Fact]
        public void Covers_IgnoresClosedWindows()
        {
            AddBoth();
            Assert.True(_stack.Covers(FrontOnly));
            Assert.True(_stack.Covers(BackOnly));
            Assert.False(_stack.Covers(Outside));

            _f.IsOpen = false;
            Assert.False(_stack.Covers(FrontOnly));
            Assert.True(_stack.Covers(BackOnly));
        }

        [Fact]
        public void DrawHover_AgreesWithUpdate()
        {
            AddBoth();
            Frame(Overlap);
            Draw();
            Assert.True(_f.DrawHover);
            Assert.False(_b.DrawHover);
            Assert.False(_pointer.IsOccluded(Overlap));
        }

        [Fact]
        public void AfterUpdate_WindowsStayOccludedAndBlocked()
        {
            AddBoth();
            Frame(Outside);
            Assert.True(_pointer.IsOccluded(BackOnly));
            Assert.True(_pointer.IsBlocked(BackOnly));
            Assert.True(_pointer.IsOccluded(FrontOnly));
            Assert.True(_pointer.IsBlocked(FrontOnly));
            Assert.False(_pointer.IsOccluded(Outside));
            Assert.False(_pointer.IsBlocked(Outside));
        }

        [Fact]
        public void BringToFront_And_Remove()
        {
            AddBoth();
            _stack.BringToFront(_b);
            AssertOrder(_f, _b);

            _stack.Remove(_f);
            AssertOrder(_b);
            Frame(Outside);
            Draw();
            Assert.Equal(new[] { "update:B", "draw:B" }, _log);
        }
    }
}
