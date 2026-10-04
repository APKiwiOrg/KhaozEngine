using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// The opt-in tap tolerance on <see cref="PointerGesture"/>: straight-line distance from the press point with a
    /// time grace, instead of the strict path length. Frames are 1/32 s unless stated, exact in binary, so the clock
    /// reaches the 0.25 s grace on frame 8 (the press frame is frame 0) without rounding.
    /// </summary>
    public class PointerGestureTapToleranceTests
    {
        const float FrameSeconds = 1f / 32f;

        static readonly PointerTapTolerance Tolerance = new(4f, 0.25f, 8f);
        static readonly IReadOnlySet<Key> NoKeys = new HashSet<Key>();
        static readonly IReadOnlySet<MouseButton> NoButtons = new HashSet<MouseButton>();
        static readonly Vector2 Start = new(100f, 50f);

        readonly record struct Input(bool Down, Vector2 Delta, float Elapsed = FrameSeconds, bool Blocked = false);

        readonly record struct Output(PointerGesturePhase Phase, bool Tap, Vector2 TapPosition, Vector2 DragDelta);

        static PointerGesture New(PointerTapTolerance? tolerance = null) => new(MouseButton.Right, tolerance ?? Tolerance);

        static InputState Frame(MouseButton button, Input frame, Vector2 cursor)
        {
            IReadOnlySet<MouseButton> held = frame.Down ? new HashSet<MouseButton> { button } : NoButtons;
            return new InputState(NoKeys, NoKeys, NoKeys, held, NoButtons, cursor, frame.Delta, 0, 960, 540);
        }

        // Runs a script and records the gesture after every frame. The cursor is advanced by each delta so the press
        // origin and the travel cannot disagree.
        static List<Output> Run(PointerGesture gesture, IEnumerable<Input> script, bool timed = true)
        {
            var outputs = new List<Output>();
            Vector2 cursor = Start;
            foreach (Input frame in script)
            {
                cursor += frame.Delta;
                InputState input = Frame(gesture.Button, frame, cursor);
                if (timed) gesture.Advance(input, frame.Blocked, frame.Elapsed);
                else gesture.Advance(input, frame.Blocked);
                outputs.Add(new Output(gesture.Phase, gesture.TapThisFrame, gesture.TapPosition, gesture.DragDelta));
            }

            return outputs;
        }

        static IEnumerable<Input> Held(float pointsPerFrame, int frames)
        {
            for (int i = 0; i < frames; i++) yield return new Input(true, new Vector2(pointsPerFrame, 0f));
        }

        static IEnumerable<Input> Then(IEnumerable<Input> script, params Input[] more)
        {
            foreach (Input frame in script) yield return frame;
            foreach (Input frame in more) yield return frame;
        }

        static readonly Input Release = new(false, Vector2.Zero);

        // A press at rest, then +3, -3, +3, -3, +3, -3, +2 points at 0.1 s a frame: 20 points of path, never more
        // than 3 from the press, then release.
        static IEnumerable<Input> Wobble()
        {
            yield return new Input(true, Vector2.Zero, 0.1f);
            float[] steps = { 3f, -3f, 3f, -3f, 3f, -3f, 2f };
            foreach (float step in steps) yield return new Input(true, new Vector2(step, 0f), 0.1f);
            yield return new Input(false, Vector2.Zero, 0.1f);
        }

        static IEnumerable<Input> ShortMove() => Then(Held(2f, 3), Release);

        static IEnumerable<Input> SlowDrag() => Then(Held(0.875f, 18), Release);

        static IEnumerable<Input> FastMove() => Then(Held(3f, 3), Release);

        [Fact]
        public void DefaultGestureIsUnchanged()
        {
            Input[][] scripts =
            {
                new[] { new Input(true, Vector2.Zero), new Input(true, Vector2.Zero), Release },
                new[] { new Input(true, Vector2.Zero), new Input(true, new Vector2(3f, 0f)), new Input(true, new Vector2(-2f, 0f)), Release },
                new[] { new Input(true, Vector2.Zero), new Input(true, new Vector2(5f, 0f)), new Input(true, new Vector2(1f, 0f)), Release },
                new[] { new Input(true, Vector2.Zero, Blocked: true), new Input(true, new Vector2(6f, 0f)), Release },
            };

            foreach (Input[] script in scripts)
            {
                var untimed = new PointerGesture(MouseButton.Right);
                var timed = new PointerGesture(MouseButton.Right);
                Assert.Null(timed.TapTolerance);

                // A long frame time, so a grace that leaked into the default would change the outcome.
                var slow = Array.ConvertAll(script, f => f with { Elapsed = 0.3f });
                Assert.Equal(Run(untimed, script, timed: false), Run(timed, slow));
            }
        }

        [Fact]
        public void WobbleEndingNearThePressIsATap()
        {
            List<Output> outputs = Run(New(), Wobble());
            Output release = outputs[^1];
            Assert.True(release.Tap);
            Assert.Equal(Start, release.TapPosition);
            Assert.All(outputs, o => Assert.Equal(Vector2.Zero, o.DragDelta));
            Assert.All(outputs[..^1], o => Assert.Equal(PointerGesturePhase.Pending, o.Phase));
        }

        [Fact]
        public void ShortMoveReleasedWithinTheGraceIsATap()
        {
            List<Output> outputs = Run(New(), ShortMove());
            Assert.All(outputs[..3], o => Assert.Equal(PointerGesturePhase.Pending, o.Phase));
            Assert.True(outputs[3].Tap);
        }

        [Fact]
        public void SlowDragCrossesWhenTheGraceEnds()
        {
            List<Output> outputs = Run(New(), SlowDrag());
            for (int i = 0; i <= 7; i++) Assert.Equal(PointerGesturePhase.Pending, outputs[i].Phase);
            for (int i = 8; i < 18; i++) Assert.Equal(PointerGesturePhase.Dragging, outputs[i].Phase);
            Assert.Equal(new Vector2(7.875f, 0f), outputs[8].DragDelta);
            Assert.Equal(new Vector2(0.875f, 0f), outputs[9].DragDelta);
            Assert.DoesNotContain(outputs, o => o.Tap);
        }

        [Fact]
        public void NothingReachesTheConsumerBeforeTheCrossing()
        {
            foreach (IEnumerable<Input> script in new[] { Wobble(), ShortMove(), SlowDrag(), FastMove() })
            {
                foreach (Output o in Run(New(), script))
                {
                    if (o.Phase != PointerGesturePhase.Dragging) Assert.Equal(Vector2.Zero, o.DragDelta);
                }
            }
        }

        [Fact]
        public void GraceDecidedCatchUpIsCapped()
        {
            List<Output> capped = Run(New(Tolerance with { CatchUpLimitPoints = 2f }), SlowDrag());
            Assert.Equal(PointerGesturePhase.Pending, capped[7].Phase);
            Assert.Equal(PointerGesturePhase.Dragging, capped[8].Phase);
            Assert.Equal(2f, capped[8].DragDelta.X, 1e-6f);
            Assert.Equal(0f, capped[8].DragDelta.Y, 1e-6f);

            List<Output> dropped = Run(New(Tolerance with { CatchUpLimitPoints = 0f }), SlowDrag());
            Assert.Equal(PointerGesturePhase.Dragging, dropped[8].Phase);
            Assert.Equal(Vector2.Zero, dropped[8].DragDelta);
            Assert.Equal(new Vector2(0.875f, 0f), dropped[9].DragDelta);
        }

        [Fact]
        public void FastMoveCrossesInsideTheGraceWithFullCatchUp()
        {
            // The cap applies only when the grace decides the press, so a move past the grace distance keeps it all.
            List<Output> outputs = Run(New(Tolerance with { CatchUpLimitPoints = 0f }), FastMove());
            Assert.Equal(PointerGesturePhase.Pending, outputs[1].Phase);
            Assert.Equal(PointerGesturePhase.Dragging, outputs[2].Phase);
            Assert.Equal(new Vector2(9f, 0f), outputs[2].DragDelta);
            Assert.False(outputs[3].Tap);
        }

        [Fact]
        public void HitchOnTheReleaseFrameStillTaps()
        {
            List<Output> outputs = Run(New(), Then(Held(2f, 3), new Input(false, Vector2.Zero, 0.3f)));
            Assert.True(outputs[3].Tap);
            Assert.Equal(Start + new Vector2(2f, 0f), outputs[3].TapPosition);
        }

        [Fact]
        public void TwoArgumentAdvanceThrowsWithATolerance()
        {
            PointerGesture gesture = New();
            Assert.Same(Tolerance, gesture.TapTolerance);
            Assert.Equal(Tolerance.DistancePoints, gesture.ThresholdPixels);
            Assert.Throws<InvalidOperationException>(
                () => gesture.Advance(Frame(gesture.Button, new Input(true, Vector2.Zero), Start), uiBlocked: false));
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(-0.01f)]
        public void ElapsedSecondsMustBeFiniteAndNotNegative(float elapsed)
        {
            InputState input = Frame(MouseButton.Right, new Input(true, Vector2.Zero), Start);
            Assert.ThrowsAny<ArgumentException>(() => New().Advance(input, false, elapsed));
            Assert.ThrowsAny<ArgumentException>(() => new PointerGesture(MouseButton.Right).Advance(input, false, elapsed));
        }

        [Theory]
        [InlineData(0f, 0.25f, 8f, float.PositiveInfinity)]
        [InlineData(float.NaN, 0.25f, 8f, float.PositiveInfinity)]
        [InlineData(4f, -0.25f, 8f, float.PositiveInfinity)]
        [InlineData(4f, 0.25f, 3f, float.PositiveInfinity)]
        [InlineData(4f, 0.25f, 8f, -1f)]
        public void InvalidTolerancesAreRefused(float distance, float grace, float graceDistance, float catchUpLimit)
        {
            var tolerance = new PointerTapTolerance(distance, grace, graceDistance) { CatchUpLimitPoints = catchUpLimit };
            ArgumentException refused = Assert.Throws<ArgumentException>(() => new PointerGesture(MouseButton.Right, tolerance));
            Assert.Equal("tapTolerance", refused.ParamName);
        }
    }
}
