using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    public class PointerOcclusionTests
    {
        static readonly Rect A = new(100, 100, 200, 200);
        static readonly Rect C = new(150, 150, 100, 100);
        static readonly Vector2 Covered = new(160, 160);
        static readonly Vector2 Uncovered = new(120, 120);
        static readonly HashSet<MouseButton> None = new();
        static readonly HashSet<MouseButton> Left = new() { MouseButton.Left };
        static readonly HashSet<MouseButton> Right = new() { MouseButton.Right };

        // One per test-class instance, so each fact derives its own press and release edges.
        readonly MouseFrames _mouse = new();

        InputState Frame(Vector2 at, IReadOnlySet<MouseButton> held)
        {
            var (edgePressed, edgeReleased) = _mouse.Advance(held);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                held, edgePressed, at, Vector2.Zero, 0, 960, 540,
                mouseReleased: edgeReleased);
        }

        // A press and release that both land inside one frame, built as PointerTests.TapFrame does.
        static InputState TapFrame(Vector2 at, MouseButton button) => new(
            new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton> { button },
            at, Vector2.Zero, 0, 960, 540,
            mouseReleased: new HashSet<MouseButton> { button });

        // Press at the origin, then release at the release point, occluding C on every frame when asked.
        Pointer Tap(Vector2 origin, Vector2 release, bool occlude)
        {
            var p = new Pointer();
            p.Update(Frame(origin, None));
            p.Update(Frame(origin, Left));
            if (occlude) p.Occlude(C);
            p.Update(Frame(release, None));
            if (occlude) p.Occlude(C);
            return p;
        }

        [Fact]
        public void NoOccluders_QueriesUnchanged()
        {
            var p = Tap(Covered, Covered, occlude: false);
            Assert.True(p.IsTapIn(A));
        }

        [Fact]
        public void TapIn_MissesWhenOriginOrReleaseOccluded()
        {
            Assert.False(Tap(Covered, Covered, occlude: true).IsTapIn(A));
            Assert.False(Tap(Uncovered, Covered, occlude: true).IsTapIn(A));
            Assert.True(Tap(Uncovered, Uncovered, occlude: true).IsTapIn(A));
        }

        [Fact]
        public void TapFromTo_MissesWhenOriginOrReleaseOccluded()
        {
            Assert.False(Tap(Covered, Uncovered, occlude: true).IsTapFromTo(A, A));
            Assert.False(Tap(Uncovered, Covered, occlude: true).IsTapFromTo(A, A));
            Assert.True(Tap(Uncovered, Uncovered, occlude: true).IsTapFromTo(A, A));
            Assert.True(Tap(Covered, Covered, occlude: false).IsTapFromTo(A, A));
        }

        [Fact]
        public void PressingIn_MissesWhenOriginOrPositionOccluded()
        {
            var p = new Pointer();
            p.Update(Frame(Covered, None));
            p.Update(Frame(Covered, Left));
            p.Occlude(C);
            Assert.False(p.IsPressingIn(A));

            var q = new Pointer();
            q.Update(Frame(Uncovered, None));
            q.Update(Frame(Uncovered, Left));
            q.Update(Frame(Covered, Left));
            q.Occlude(C);
            Assert.False(q.IsPressingIn(A));

            var r = new Pointer();
            r.Update(Frame(Uncovered, None));
            r.Update(Frame(Uncovered, Left));
            r.Occlude(C);
            Assert.True(r.IsPressingIn(A));
        }

        [Fact]
        public void DragFromUncoveredOrigin_KeepsAnsweringWhenCursorEntersOccludedArea()
        {
            var p = new Pointer();
            p.Update(Frame(Uncovered, None));
            p.Update(Frame(Uncovered, Left));
            p.Occlude(C);
            Assert.True(p.IsDraggingIn(A));

            p.Update(Frame(Covered, Left));
            p.Occlude(C);
            Assert.True(p.IsOccluded(p.Position));
            Assert.True(p.IsDraggingIn(A));
            Assert.NotEqual(Vector2.Zero, p.GetDragDelta(A));
        }

        [Fact]
        public void HoverAndPointerIn_MissInOccludedArea()
        {
            var p = new Pointer();
            p.Update(Frame(Covered, None));
            p.Occlude(C);
            Assert.False(p.IsHoveringIn(A));
            Assert.False(p.IsPointerIn(A));

            p.Update(Frame(Uncovered, None));
            p.Occlude(C);
            Assert.True(p.IsHoveringIn(A));
            Assert.True(p.IsPointerIn(A));
        }

        [Fact]
        public void PressOriginQueries_MissWhenOriginOccluded()
        {
            var p = new Pointer();
            p.Update(Frame(Covered, None));
            p.Update(Frame(Covered, Left));
            p.Update(Frame(Covered + new Vector2(10, 10), Left));
            p.Occlude(C);
            Assert.False(p.IsDragStartIn(A));
            Assert.False(p.IsDraggingIn(A));
            Assert.Equal(Vector2.Zero, p.GetDragDelta(A));

            var q = new Pointer();
            q.Update(Frame(Uncovered, None));
            q.Update(Frame(Uncovered, Left));
            q.Update(Frame(Uncovered + new Vector2(10, 10), Left));
            q.Occlude(C);
            Assert.True(q.IsDragStartIn(A));
            Assert.True(q.IsDraggingIn(A));
            Assert.NotEqual(Vector2.Zero, q.GetDragDelta(A));
        }

        [Fact]
        public void RightQueries_MissWhenOccluded()
        {
            var p = new Pointer();
            p.Update(Frame(Covered, None));
            p.Update(Frame(Covered, Right));
            p.Occlude(C);
            Assert.False(p.IsRightPressingIn(A));
            p.Update(Frame(Covered, None));
            p.Occlude(C);
            Assert.False(p.IsRightTapIn(A));
        }

        [Fact]
        public void ReleasedOutside_TrueWhenReleaseOccluded()
        {
            var p = Tap(Uncovered, Covered, occlude: true);
            Assert.True(p.IsReleasedOutside(A));
        }

        [Fact]
        public void OccludeAll_MissesEverywhere()
        {
            var p = new Pointer();
            p.Update(Frame(Uncovered, None));
            p.OccludeAll();
            Assert.False(p.IsPointerIn(A));
            Assert.True(p.IsOccluded(new Vector2(-5000, 9000)));
        }

        [Fact]
        public void Occlusion_ClearsOnUpdate_AndOnClear()
        {
            var p = new Pointer();
            p.Update(Frame(Uncovered, None));
            p.Occlude(C);
            Assert.True(p.IsOccluded(Covered));
            p.Update(Frame(Uncovered, None));
            Assert.False(p.IsOccluded(Covered));

            p.Occlude(C);
            p.ClearOcclusion();
            Assert.False(p.IsOccluded(Covered));

            p.OccludeAll();
            p.ClearOcclusion();
            Assert.False(p.IsOccluded(Covered));
        }

        [Fact]
        public void BlockRegion_Unaffected()
        {
            var p = new Pointer();
            p.Update(Frame(Covered, None));
            p.Occlude(C);
            Assert.False(p.IsBlocked(Covered));

            var q = Tap(Covered, Covered, occlude: false);
            q.BlockRegion(C);
            Assert.True(q.IsTapIn(A));
        }

        [Fact]
        public void RightPressOriginFresh_MatchesLeft()
        {
            var p = new Pointer();
            p.Update(Frame(Uncovered, None));
            Assert.False(p.IsRightPressOriginFresh);
            p.Update(Frame(Uncovered, Right));
            Assert.True(p.IsRightPressOriginFresh);
            p.Update(Frame(Uncovered, Right));
            Assert.False(p.IsRightPressOriginFresh);
            p.Update(Frame(Uncovered, None));
            Assert.False(p.IsRightPressOriginFresh);

            p.Update(TapFrame(Uncovered, MouseButton.Right));
            Assert.True(p.IsRightPressOriginFresh);
            Assert.False(p.IsPressOriginFresh);
        }
    }
}
