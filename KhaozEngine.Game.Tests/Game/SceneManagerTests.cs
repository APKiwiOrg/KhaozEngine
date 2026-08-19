using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Game
{
    /// <summary>
    /// Headless coverage for <see cref="SceneManager"/>: lifecycle ordering, update gating (overlays freeze what
    /// is below unless UpdateBelow), draw visibility via the internal <c>FirstVisibleIndex()</c> probe, and
    /// mutation-safe deferred transitions. No GPU: scenes record an ordered log instead of drawing.
    /// </summary>
    public class SceneManagerTests
    {
        // Records OnEnter/OnExit/OnUpdate (with a name) so tests can assert call counts + ordering.
        sealed class FakeScene : GameScene
        {
            readonly List<string> _log;
            readonly string _name;

            public int Enters, Exits, Updates, Resizes, UiDraws;
            public int LastResizeW, LastResizeH;
            public System.Action<float>? OnUpdateHook;

            public FakeScene(string name, List<string> log)
            {
                _name = name;
                _log = log;
            }

            public override void OnEnter() { Enters++; _log.Add($"{_name}.Enter"); }
            public override void OnExit() { Exits++; _log.Add($"{_name}.Exit"); }
            public override void OnUpdate(float dt) { Updates++; _log.Add($"{_name}.Update"); OnUpdateHook?.Invoke(dt); }
            public override void OnResize(int w, int h) { Resizes++; LastResizeW = w; LastResizeH = h; }
            // The routing passes the batch straight through without touching it, so tests can drive it with a null
            // batch and just count the calls per visible scene.
            public override void OnDrawUi(KhaozEngine.Render2D.SpriteBatch batch) { UiDraws++; _log.Add($"{_name}.DrawUi"); }
        }

        static (SceneManager m, List<string> log) NewManager()
        {
            return (new SceneManager(), new List<string>());
        }

        // One per test-class instance (xUnit builds a fresh instance per fact), so the mouse press and
        // release edges derive from this test's own frame sequence and nothing crosses between tests.
        readonly MouseFrames _mouse = new();

        InputState Frame(Vector2 pos, bool leftDown)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            var (edgePressed, edgeReleased) = _mouse.Advance(down);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                down, edgePressed, pos, Vector2.Zero, 0, 960, 540, mouseReleased: edgeReleased);
        }

        // A pointer mid-gesture: pressed and released inside `rect`, so IsTapIn(rect) is true until consumed.
        Pointer MidTapPointer(Rect rect)
        {
            var at = new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
            var p = new Pointer();
            p.Update(Frame(at, false));
            p.Update(Frame(at, true));    // press inside
            p.Update(Frame(at, false));   // release inside -> a complete tap
            return p;
        }

        [Fact]
        public void Push_SetsActive_CallsOnEnter_AndManager()
        {
            var (m, log) = NewManager();
            var a = new FakeScene("A", log);

            m.Push(a);

            Assert.Same(a, m.Active);
            Assert.Equal(1, m.Count);
            Assert.Equal(1, a.Enters);
            Assert.Same(m, a.Manager);
            Assert.Equal(new[] { "A.Enter" }, log);
        }

        [Fact]
        public void Pop_CallsOnExit_RestoresPreviousActive_ClearsManager()
        {
            var (m, log) = NewManager();
            var a = new FakeScene("A", log);
            var b = new FakeScene("B", log);
            m.Push(a);
            m.Push(b);

            m.Pop();

            Assert.Same(a, m.Active);
            Assert.Equal(1, m.Count);
            Assert.Equal(1, b.Exits);
            Assert.Null(b.Manager);
            Assert.Same(m, a.Manager);
        }

        [Fact]
        public void Replace_SwapsTop_BelowUntouched()
        {
            var (m, log) = NewManager();
            var a = new FakeScene("A", log);
            var b = new FakeScene("B", log);
            var c = new FakeScene("C", log);
            m.Push(a);
            m.Push(b);
            log.Clear();

            m.Replace(c);

            Assert.Equal(2, m.Count);
            Assert.Same(c, m.Active);
            Assert.Same(a, m.Scenes[0]);
            Assert.Equal(1, b.Exits);
            Assert.Equal(1, c.Enters);
            Assert.Equal(0, a.Exits); // below the swapped top, untouched
            Assert.Equal(new[] { "B.Exit", "C.Enter" }, log);
        }

        [Fact]
        public void SwitchTo_ClearsAllTopDown_ThenPushes()
        {
            var (m, log) = NewManager();
            var a = new FakeScene("A", log);
            var b = new FakeScene("B", log);
            var c = new FakeScene("C", log);
            m.Push(a);
            m.Push(b);
            log.Clear();

            m.SwitchTo(c);

            Assert.Equal(1, m.Count);
            Assert.Same(c, m.Active);
            // top-down exits (B then A) then the new scene enters.
            Assert.Equal(new[] { "B.Exit", "A.Exit", "C.Enter" }, log);
            Assert.Null(a.Manager);
            Assert.Null(b.Manager);
        }

        [Fact]
        public void Update_TopAlwaysUpdates_BelowFrozenWhenUpdateBelowFalse()
        {
            var (m, log) = NewManager();
            var a = new FakeScene("A", log);
            var b = new FakeScene("B", log) { UpdateBelow = false };
            m.Push(a);
            m.Push(b);

            m.Update(0.016f);

            Assert.Equal(1, b.Updates);
            Assert.Equal(0, a.Updates); // frozen below the opaque overlay
        }

        [Fact]
        public void Update_BelowUpdatesWhenUpdateBelowTrue()
        {
            var (m, log) = NewManager();
            var a = new FakeScene("A", log);
            var b = new FakeScene("B", log) { UpdateBelow = true };
            m.Push(a);
            m.Push(b);

            m.Update(0.016f);

            Assert.Equal(1, b.Updates);
            Assert.Equal(1, a.Updates);
        }

        [Fact]
        public void Update_DescentStopsAtFirstSceneThatDoesNotPassThrough()
        {
            // [A, B, C(top)] with C.UpdateBelow=true, B.UpdateBelow=false:
            // C and B update, A does NOT (descent stops at B).
            var (m, log) = NewManager();
            var a = new FakeScene("A", log);
            var b = new FakeScene("B", log) { UpdateBelow = false };
            var c = new FakeScene("C", log) { UpdateBelow = true };
            m.Push(a);
            m.Push(b);
            m.Push(c);

            m.Update(0.016f);

            Assert.Equal(1, c.Updates);
            Assert.Equal(1, b.Updates);
            Assert.Equal(0, a.Updates);
        }

        [Fact]
        public void FirstVisibleIndex_DrawBelowTrue_RevealsBelow()
        {
            var (m, log) = NewManager();
            var a = new FakeScene("A", log);
            var b = new FakeScene("B", log) { DrawBelow = true };
            m.Push(a);
            m.Push(b);

            Assert.Equal(0, m.FirstVisibleIndex()); // both A and B visible
        }

        [Fact]
        public void FirstVisibleIndex_DrawBelowFalse_HidesBelow()
        {
            var (m, log) = NewManager();
            var a = new FakeScene("A", log);
            var b = new FakeScene("B", log) { DrawBelow = false };
            m.Push(a);
            m.Push(b);

            Assert.Equal(1, m.FirstVisibleIndex()); // only B (the top) visible
        }

        [Fact]
        public void FirstVisibleIndex_DescendsThroughMultipleOverlays()
        {
            var (m, log) = NewManager();
            var a = new FakeScene("A", log);
            var b = new FakeScene("B", log) { DrawBelow = true };
            var c = new FakeScene("C", log) { DrawBelow = true };
            m.Push(a);
            m.Push(b);
            m.Push(c);

            Assert.Equal(0, m.FirstVisibleIndex());
        }

        [Fact]
        public void DrawUi_routes_the_point_space_pass_to_the_visible_scenes_only()
        {
            var (m, log) = NewManager();
            var a = new FakeScene("A", log);
            var b = new FakeScene("B", log) { DrawBelow = false };   // hides A
            m.Push(a);
            m.Push(b);

            m.DrawUi(null!);   // batch is passed through untouched by the routing

            Assert.Equal(0, a.UiDraws);   // hidden -> not drawn
            Assert.Equal(1, b.UiDraws);   // top -> drawn
        }

        [Fact]
        public void FirstVisibleIndex_EmptyStack_ReturnsCount()
        {
            var (m, _) = NewManager();
            Assert.Equal(m.Count, m.FirstVisibleIndex()); // == 0; draw loops run zero iterations
        }

        [Fact]
        public void DeferredPop_FromWithinOnUpdate_DoesNotCorruptStack()
        {
            var (m, log) = NewManager();
            var a = new FakeScene("A", log);
            var b = new FakeScene("B", log) { UpdateBelow = false };
            m.Push(a);
            m.Push(b);
            b.OnUpdateHook = _ => m.Pop(); // request pop mid-update
            log.Clear();

            m.Update(0.016f);

            // The pop applied AFTER the pass: B updated once, its OnExit ran once, A is now active.
            Assert.Equal(1, b.Updates);
            Assert.Equal(1, b.Exits);
            Assert.Same(a, m.Active);
            Assert.Equal(1, m.Count);
            // B updated during the pass (pre-pop stack), then exited at drain time.
            Assert.Equal(new[] { "B.Update", "B.Exit" }, log);
        }

        [Fact]
        public void DeferredPush_FromWithinOnUpdate_AppliesAfterPass()
        {
            var (m, log) = NewManager();
            var a = new FakeScene("A", log);
            var b = new FakeScene("B", log);
            m.Push(a);
            a.OnUpdateHook = _ =>
            {
                if (m.Count == 1) m.Push(b); // push a new scene mid-update; must not be updated this pass
            };
            log.Clear();

            m.Update(0.016f);

            Assert.Equal(2, m.Count);
            Assert.Same(b, m.Active);
            Assert.Equal(1, a.Updates);
            Assert.Equal(0, b.Updates);   // pushed AFTER the pass; not updated this frame
            Assert.Equal(1, b.Enters);
            // A updated during the pass, B entered at drain time (after the pass).
            Assert.Equal(new[] { "A.Update", "B.Enter" }, log);
        }

        [Fact]
        public void DeferredTransitions_PreserveCallOrder()
        {
            var (m, log) = NewManager();
            var a = new FakeScene("A", log);
            var b = new FakeScene("B", log) { UpdateBelow = true };
            var c = new FakeScene("C", log);
            var d = new FakeScene("D", log);
            m.Push(a);
            m.Push(b);
            // From within the pass, queue Pop() then Push(c) then Push(d), in that order.
            b.OnUpdateHook = _ =>
            {
                if (m.Active == b) // guard so only the first frame queues
                {
                    m.Pop();
                    m.Push(c);
                    m.Push(d);
                }
            };
            log.Clear();

            m.Update(0.016f);

            // Both A and B update (B.UpdateBelow=true); then drain in order: B.Exit, C.Enter, D.Enter.
            Assert.Equal(new[] { "B.Update", "A.Update", "B.Exit", "C.Enter", "D.Enter" }, log);
            Assert.Equal(3, m.Count); // A, C, D
            Assert.Same(d, m.Active);
            Assert.Same(a, m.Scenes[0]);
        }

        [Fact]
        public void Resize_ForwardsToAllScenes_AndSetsFrameSize()
        {
            var (m, log) = NewManager();
            var a = new FakeScene("A", log);
            var b = new FakeScene("B", log);
            m.Push(a);
            m.Push(b);

            m.Resize(1280, 720);

            Assert.Equal(1280, m.FrameWidth);
            Assert.Equal(720, m.FrameHeight);
            Assert.Equal(1, a.Resizes);
            Assert.Equal(1, b.Resizes);
            Assert.Equal(1280, a.LastResizeW);
            Assert.Equal(720, b.LastResizeH);
        }

        [Fact]
        public void Push_consumes_the_in_flight_pointer_gesture()
        {
            // Campaign-map click-through: the release that pushes an overlay must not also register as a tap on
            // an overlay button drawn the same frame under the same press-origin.
            var rect = new Rect(100, 100, 120, 40);
            var p = MidTapPointer(rect);
            Assert.True(p.IsTapIn(rect));            // the gesture would tap...

            var (m, log) = NewManager();
            m.Pointer = p;
            m.Push(new FakeScene("overlay", log));   // ...but pushing on that release claims it

            Assert.True(p.IsConsumed);
            Assert.False(p.IsTapIn(rect));           // overlay widgets see no tap
        }

        [Fact]
        public void Pop_consumes_the_in_flight_pointer_gesture()
        {
            var rect = new Rect(100, 100, 120, 40);
            var (m, log) = NewManager();
            m.Push(new FakeScene("a", log));
            m.Push(new FakeScene("b", log));
            var p = MidTapPointer(rect);
            m.Pointer = p;                            // wired after the setup pushes, so only the Pop consumes it
            Assert.True(p.IsTapIn(rect));

            m.Pop();

            Assert.True(p.IsConsumed);
            Assert.False(p.IsTapIn(rect));
        }

        [Fact]
        public void Transitions_are_safe_when_no_pointer_is_set()
        {
            var (m, log) = NewManager();   // Pointer left null
            m.Push(new FakeScene("a", log));
            m.Pop();
            Assert.Equal(0, m.Count);      // null-safe consume: no throw
        }

        [Fact]
        public void PopOnEmpty_IsSafeNoOp()
        {
            var (m, log) = NewManager();
            m.Pop();
            Assert.Equal(0, m.Count);
            Assert.Null(m.Active);
            Assert.Empty(log);
        }

        [Fact]
        public void ClearOnEmpty_IsSafeNoOp()
        {
            var (m, log) = NewManager();
            m.Clear();
            Assert.Equal(0, m.Count);
            Assert.Null(m.Active);
            Assert.Empty(log);
        }

        [Fact]
        public void Clear_PopsAllTopDown()
        {
            var (m, log) = NewManager();
            var a = new FakeScene("A", log);
            var b = new FakeScene("B", log);
            var c = new FakeScene("C", log);
            m.Push(a);
            m.Push(b);
            m.Push(c);
            log.Clear();

            m.Clear();

            Assert.Equal(0, m.Count);
            Assert.Null(m.Active);
            Assert.Equal(new[] { "C.Exit", "B.Exit", "A.Exit" }, log);
        }
    }
}
