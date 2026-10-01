using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// The per-button tap and drag splitter. Pure and headless: the gesture owns no camera and no input source, so
    /// every case below is one <see cref="InputState"/> a frame.
    /// </summary>
    public class PointerGestureTests
    {
        const float Threshold = 4f;

        static readonly IReadOnlySet<Key> NoKeys = new HashSet<Key>();
        static readonly IReadOnlySet<MouseButton> NoButtons = new HashSet<MouseButton>();

        static PointerGesture New(float thresholdPixels = Threshold) => new(MouseButton.Right, thresholdPixels);

        static InputState Frame(IReadOnlySet<MouseButton> down, Vector2 cursor, Vector2 delta, bool windowFocused = true) =>
            new(NoKeys, NoKeys, NoKeys, down, NoButtons, cursor, delta, 0, 960, 540, windowFocused: windowFocused);

        // One frame: button state, this frame's delta, and where the cursor ended up. The cursor is advanced by the
        // delta so the press origin and the travel cannot silently disagree, which is the bug the TapPosition case
        // below exists to catch.
        static void Step(PointerGesture gesture, bool down, ref Vector2 cursor, Vector2 delta, bool uiBlocked = false)
        {
            cursor += delta;
            IReadOnlySet<MouseButton> held = down ? new HashSet<MouseButton> { gesture.Button } : NoButtons;
            gesture.Advance(Frame(held, cursor, delta), uiBlocked);
        }

        [Fact]
        public void PressAndReleaseWithoutMoving_TapsForExactlyOneFrame()
        {
            PointerGesture gesture = New();
            var cursor = new Vector2(100f, 50f);

            Step(gesture, down: true, ref cursor, Vector2.Zero);
            Assert.Equal(PointerGesturePhase.Pending, gesture.Phase);
            Assert.False(gesture.TapThisFrame);

            Step(gesture, down: false, ref cursor, Vector2.Zero);
            Assert.True(gesture.TapThisFrame);
            Assert.Equal(PointerGesturePhase.Idle, gesture.Phase);

            Step(gesture, down: false, ref cursor, Vector2.Zero);
            Assert.False(gesture.TapThisFrame);      // exactly one frame, never latched
        }

        [Fact]
        public void Tap_ReportsThePressOriginNotTheRelease()
        {
            PointerGesture gesture = New();
            var cursor = new Vector2(200f, 300f);

            Step(gesture, down: true, ref cursor, Vector2.Zero);
            Step(gesture, down: true, ref cursor, new Vector2(1f, 1f));   // a tremor, still under the threshold
            Step(gesture, down: false, ref cursor, Vector2.Zero);

            Assert.True(gesture.TapThisFrame);
            Assert.Equal(new Vector2(200f, 300f), gesture.TapPosition);
            Assert.Equal(new Vector2(201f, 301f), cursor);                // the release really was somewhere else
        }

        [Fact]
        public void DragPastTheThreshold_AllowsOrbitAndNeverTaps()
        {
            PointerGesture gesture = New();
            var cursor = new Vector2(10f, 10f);

            Step(gesture, down: true, ref cursor, Vector2.Zero);
            Step(gesture, down: true, ref cursor, new Vector2(20f, 0f));
            Assert.Equal(PointerGesturePhase.Dragging, gesture.Phase);

            Step(gesture, down: false, ref cursor, Vector2.Zero);
            Assert.False(gesture.TapThisFrame);      // a released drag is not a tap
            Assert.Equal(PointerGesturePhase.Idle, gesture.Phase);
        }

        [Fact]
        public void Travel_AccumulatesAcrossFrames_RatherThanPerFrame()
        {
            PointerGesture gesture = New();
            var cursor = new Vector2(0f, 0f);
            var step = new Vector2(2f, 0f);          // every frame is under the threshold on its own

            Step(gesture, down: true, ref cursor, Vector2.Zero);
            Step(gesture, down: true, ref cursor, step);
            Assert.Equal(PointerGesturePhase.Pending, gesture.Phase);     // 2 travelled
            Step(gesture, down: true, ref cursor, step);
            Assert.Equal(PointerGesturePhase.Pending, gesture.Phase);     // 4 travelled, the threshold is not exceeded yet
            Step(gesture, down: true, ref cursor, step);
            Assert.Equal(PointerGesturePhase.Dragging, gesture.Phase);    // 6 travelled
        }

        [Fact]
        public void Travel_IsPathLengthNotNetDisplacement()
        {
            PointerGesture gesture = New();
            var cursor = new Vector2(0f, 0f);

            Step(gesture, down: true, ref cursor, Vector2.Zero);
            Step(gesture, down: true, ref cursor, new Vector2(3f, 0f));
            Step(gesture, down: true, ref cursor, new Vector2(-3f, 0f));  // back where it started, but it moved

            Assert.Equal(PointerGesturePhase.Dragging, gesture.Phase);    // a wiggle is a drag, not a tap
            Assert.Equal(Vector2.Zero, gesture.DragDelta);                // and it caught up to a net zero swing
        }

        [Fact]
        public void CrossingTheThreshold_ReplaysTheTravelAccumulatedWhilePending()
        {
            PointerGesture gesture = New();
            var cursor = new Vector2(0f, 0f);

            Step(gesture, down: true, ref cursor, Vector2.Zero);
            Step(gesture, down: true, ref cursor, new Vector2(3f, 1f));
            Assert.Equal(Vector2.Zero, gesture.DragDelta);                // nothing drags while undecided
            Step(gesture, down: true, ref cursor, new Vector2(2f, 1f));

            // Catch-up, not a clean start: the drag ends up where a zero threshold would have put it, so it has no
            // dead zone at its front.
            Assert.Equal(new Vector2(5f, 2f), gesture.DragDelta);

            Step(gesture, down: true, ref cursor, new Vector2(7f, 0f));
            Assert.Equal(new Vector2(7f, 0f), gesture.DragDelta);         // and from there the frame passes straight through
        }

        [Fact]
        public void AStillCursorAfterTheThreshold_KeepsDragging()
        {
            PointerGesture gesture = New();
            var cursor = new Vector2(0f, 0f);

            Step(gesture, down: true, ref cursor, Vector2.Zero);
            Step(gesture, down: true, ref cursor, new Vector2(10f, 0f));
            Assert.Equal(PointerGesturePhase.Dragging, gesture.Phase);

            for (int frame = 0; frame < 5; frame++)
            {
                Step(gesture, down: true, ref cursor, Vector2.Zero);
                Assert.Equal(PointerGesturePhase.Dragging, gesture.Phase); // the decision never unwinds mid-press
            }

            Step(gesture, down: false, ref cursor, Vector2.Zero);
            Assert.False(gesture.TapThisFrame);
        }

        [Fact]
        public void ZeroThreshold_OrbitsFromFrameOneAndNeverTaps()
        {
            PointerGesture gesture = New(thresholdPixels: 0f);
            var cursor = new Vector2(0f, 0f);

            Step(gesture, down: true, ref cursor, new Vector2(1f, 2f));
            Assert.Equal(PointerGesturePhase.Dragging, gesture.Phase);
            Assert.Equal(new Vector2(1f, 2f), gesture.DragDelta);         // the press frame's own delta, unaltered

            Step(gesture, down: false, ref cursor, Vector2.Zero);
            Assert.False(gesture.TapThisFrame);
            Assert.Equal(PointerGesturePhase.Idle, gesture.Phase);
        }

        [Fact]
        public void APressThatBeganUnderTheUi_IsNeitherATapNorADrag()
        {
            PointerGesture gesture = New();
            var cursor = new Vector2(40f, 40f);

            Step(gesture, down: true, ref cursor, Vector2.Zero, uiBlocked: true);
            Assert.Equal(PointerGesturePhase.Idle, gesture.Phase);

            // The modal closes with the button still held. The press stays inert for the rest of its life: it may
            // not drag, and its release may not count as a tap.
            Step(gesture, down: true, ref cursor, new Vector2(30f, 0f));
            Assert.Equal(PointerGesturePhase.Idle, gesture.Phase);
            Assert.Equal(Vector2.Zero, gesture.DragDelta);

            Step(gesture, down: false, ref cursor, Vector2.Zero);
            Assert.False(gesture.TapThisFrame);

            // The next press is clean again.
            Step(gesture, down: true, ref cursor, Vector2.Zero);
            Assert.Equal(PointerGesturePhase.Pending, gesture.Phase);
            Step(gesture, down: false, ref cursor, Vector2.Zero);
            Assert.True(gesture.TapThisFrame);
        }

        [Fact]
        public void AModalOpeningMidDrag_StopsTheOrbit()
        {
            PointerGesture gesture = New();
            var cursor = new Vector2(0f, 0f);

            Step(gesture, down: true, ref cursor, Vector2.Zero);
            Step(gesture, down: true, ref cursor, new Vector2(10f, 0f));
            Assert.Equal(PointerGesturePhase.Dragging, gesture.Phase);

            Step(gesture, down: true, ref cursor, new Vector2(10f, 0f), uiBlocked: true);
            Assert.Equal(PointerGesturePhase.Idle, gesture.Phase);
            Assert.Equal(Vector2.Zero, gesture.DragDelta);
        }

        [Fact]
        public void LosingFocusIsBlocked()
        {
            PointerGesture gesture = New();
            var held = new HashSet<MouseButton> { gesture.Button };
            var cursor = new Vector2(60f, 60f);

            // A pending press, then the window loses focus and the accumulator releases the button. Not a tap.
            gesture.Advance(Frame(held, cursor, Vector2.Zero), uiBlocked: false);
            Assert.Equal(PointerGesturePhase.Pending, gesture.Phase);
            gesture.Advance(Frame(NoButtons, cursor, Vector2.Zero, windowFocused: false), uiBlocked: false);
            Assert.False(gesture.TapThisFrame);
            Assert.Equal(PointerGesturePhase.Idle, gesture.Phase);

            // A press that begins unfocused stays inert after refocus, even when it travels past the threshold.
            gesture.Advance(Frame(held, cursor, Vector2.Zero, windowFocused: false), uiBlocked: false);
            Assert.Equal(PointerGesturePhase.Idle, gesture.Phase);
            gesture.Advance(Frame(held, cursor, new Vector2(20f, 0f)), uiBlocked: false);
            Assert.Equal(PointerGesturePhase.Idle, gesture.Phase);
            Assert.Equal(Vector2.Zero, gesture.DragDelta);
            gesture.Advance(Frame(NoButtons, cursor, Vector2.Zero), uiBlocked: false);
            Assert.False(gesture.TapThisFrame);
            Assert.Equal(PointerGesturePhase.Idle, gesture.Phase);
        }

        [Fact]
        public void TheGestureWatchesOnlyItsOwnButton()
        {
            var gesture = new PointerGesture(MouseButton.Middle);
            Assert.Equal(MouseButton.Middle, gesture.Button);
            Assert.Equal(Threshold, gesture.ThresholdPixels);

            var others = new HashSet<MouseButton> { MouseButton.Left, MouseButton.Right };
            var cursor = new Vector2(50f, 50f);

            // Other buttons pressed, dragged and released: no press, no drag, no tap.
            gesture.Advance(Frame(others, cursor, Vector2.Zero), uiBlocked: false);
            Assert.Equal(PointerGesturePhase.Idle, gesture.Phase);
            gesture.Advance(Frame(others, cursor, new Vector2(20f, 0f)), uiBlocked: false);
            Assert.Equal(PointerGesturePhase.Idle, gesture.Phase);
            Assert.Equal(Vector2.Zero, gesture.DragDelta);
            gesture.Advance(Frame(NoButtons, cursor, Vector2.Zero), uiBlocked: false);
            Assert.False(gesture.TapThisFrame);

            // Its own button, held alongside the others, is seen.
            var withOwn = new HashSet<MouseButton> { MouseButton.Left, MouseButton.Middle, MouseButton.Right };
            gesture.Advance(Frame(withOwn, cursor, Vector2.Zero), uiBlocked: false);
            Assert.Equal(PointerGesturePhase.Pending, gesture.Phase);

            // Another button released while its own press is pending is not its release.
            var middleAndRight = new HashSet<MouseButton> { MouseButton.Middle, MouseButton.Right };
            gesture.Advance(Frame(middleAndRight, cursor, Vector2.Zero), uiBlocked: false);
            Assert.False(gesture.TapThisFrame);
            Assert.Equal(PointerGesturePhase.Pending, gesture.Phase);

            gesture.Advance(Frame(others, cursor, Vector2.Zero), uiBlocked: false);
            Assert.True(gesture.TapThisFrame);
            Assert.Equal(cursor, gesture.TapPosition);
        }
    }
}
