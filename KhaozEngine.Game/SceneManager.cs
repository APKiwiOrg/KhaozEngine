using System.Collections.Generic;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using KhaozEngine.Primitives;

namespace KhaozEngine.Game
{
    /// <summary>
    /// A stack of <see cref="GameScene"/>s (index 0 = bottom, last = top/active) with overlay support, deferred
    /// transitions, and enter/exit lifecycle. Overlays freeze what is below them unless
    /// <see cref="GameScene.UpdateBelow"/> is set, and hide what is below unless <see cref="GameScene.DrawBelow"/>
    /// is set. Push/Pop/Replace/SwitchTo/Clear called from within <see cref="Update"/> are queued and applied at
    /// the end of the update pass so the stack is never mutated mid-iteration.
    /// This is DISTINCT from the Gui <c>ScreenStack</c> (a 2D-UI-only screen stack); it is not modified here.
    /// </summary>
    public sealed class SceneManager
    {
        readonly List<GameScene> _scenes = new();
        readonly Queue<System.Action> _pending = new();
        bool _updating;

        /// <summary>This frame's raw input snapshot. Set it (with the rest of the frame context) through
        /// <see cref="SetFrameContext"/> before <see cref="Update"/>; scenes read it via <c>Manager</c>.</summary>
        public InputState Input { get; set; } = InputState.Empty;

        /// <summary>The shared bounds-aware pointer. The game sets this before <see cref="Update"/>; scenes hit-test via <c>Manager.Pointer</c>.</summary>
        public Pointer? Pointer { get; set; }

        /// <summary>The design-space viewport (or null for raw window-pixel coordinates). The game sets this before <see cref="Update"/>.</summary>
        public IDesignViewport? Viewport { get; set; }

        /// <summary>The point-space UI viewport for <see cref="GameScene.OnDrawUi"/> (or null when the game does not
        /// use a DPI-aware UI pass). The game sets this before <see cref="Update"/>; scenes read it via <c>Manager.UiViewport</c>.</summary>
        public UiViewport? UiViewport { get; set; }

        /// <summary>The point-space pointer for hit-testing in <see cref="GameScene.OnDrawUi"/> (mapped through
        /// <see cref="UiViewport"/>). The game sets this before <see cref="Update"/>; scenes hit-test via <c>Manager.UiPointer</c>.</summary>
        public Pointer? UiPointer { get; set; }

        /// <summary>This frame's window width in points. Set by the game (or by <see cref="Resize"/>).</summary>
        public int FrameWidth { get; set; }

        /// <summary>This frame's window height in points. Set by the game (or by <see cref="Resize"/>).</summary>
        public int FrameHeight { get; set; }

        /// <summary>
        /// Set this frame's whole scene context in one call, before <see cref="Update"/>. Prefer this over
        /// assigning the seven properties individually: they are settable one at a time, so a host that wires
        /// six of them and forgets the seventh compiles, runs, and leaves that one sitting at its default with
        /// nothing thrown and nothing logged. Here a forgotten field is a missing argument.
        /// <para>
        /// Two of those defaults matter more than the rest, because they are what
        /// <see cref="BootScreen"/> reads. An unset <see cref="Input"/> stays
        /// <see cref="InputState.Empty"/>, so its Enter/Escape retry-quit check never fires, and an unset
        /// <see cref="UiPointer"/> stays null, so it falls back to a pointer nobody updates and its Retry/Quit
        /// buttons draw but never register a click. The one screen meant to handle a boot failure gracefully
        /// then soft-locks, on a player's machine, at the moment it was needed.
        /// </para>
        /// </summary>
        /// <param name="input">This frame's raw input snapshot (<see cref="Input"/>).</param>
        /// <param name="pointer">The design-space pointer (<see cref="Pointer"/>), or null.</param>
        /// <param name="viewport">The design-space viewport (<see cref="Viewport"/>), or null for raw window pixels.</param>
        /// <param name="uiViewport">The point-space UI viewport (<see cref="UiViewport"/>), or null.</param>
        /// <param name="uiPointer">The point-space UI pointer (<see cref="UiPointer"/>), or null.</param>
        /// <param name="frameWidth">This frame's window width in points (<see cref="FrameWidth"/>).</param>
        /// <param name="frameHeight">This frame's window height in points (<see cref="FrameHeight"/>).</param>
        public void SetFrameContext(
            InputState input,
            Pointer? pointer,
            IDesignViewport? viewport,
            UiViewport? uiViewport,
            Pointer? uiPointer,
            int frameWidth,
            int frameHeight)
        {
            Input = input;
            Pointer = pointer;
            Viewport = viewport;
            UiViewport = uiViewport;
            UiPointer = uiPointer;
            FrameWidth = frameWidth;
            FrameHeight = frameHeight;
        }

        /// <summary>The scenes on the stack, bottom (index 0) to top.</summary>
        public IReadOnlyList<GameScene> Scenes => _scenes;

        /// <summary>The top of the stack (the active scene), or null when the stack is empty.</summary>
        public GameScene? Active => _scenes.Count > 0 ? _scenes[_scenes.Count - 1] : null;

        /// <summary>The number of scenes on the stack.</summary>
        public int Count => _scenes.Count;

        /// <summary>Add a scene on top. Queued if called during <see cref="Update"/>.</summary>
        public void Push(GameScene scene)
        {
            if (_updating) _pending.Enqueue(() => ApplyPush(scene));
            else ApplyPush(scene);
        }

        /// <summary>Remove the top scene (no-op if empty). Queued if called during <see cref="Update"/>.</summary>
        public void Pop()
        {
            if (_updating) _pending.Enqueue(ApplyPop);
            else ApplyPop();
        }

        /// <summary>Swap the top: pop the top (if any) then push <paramref name="scene"/>. Queued if called during <see cref="Update"/>.</summary>
        public void Replace(GameScene scene)
        {
            if (_updating) _pending.Enqueue(() => ApplyReplace(scene));
            else ApplyReplace(scene);
        }

        /// <summary>Hard switch: clear the whole stack (each scene's OnExit, top-down) then push <paramref name="scene"/>. Queued if called during <see cref="Update"/>.</summary>
        public void SwitchTo(GameScene scene)
        {
            if (_updating) _pending.Enqueue(() => ApplySwitchTo(scene));
            else ApplySwitchTo(scene);
        }

        /// <summary>Remove all scenes (each scene's OnExit, top-down). Queued if called during <see cref="Update"/>.</summary>
        public void Clear()
        {
            if (_updating) _pending.Enqueue(ApplyClear);
            else ApplyClear();
        }

        /// <summary>
        /// Apply any pending transitions, then update the live scenes top-down: the top scene always updates; a
        /// lower scene updates only if EVERY scene above it has <see cref="GameScene.UpdateBelow"/> set (descent
        /// stops at the first scene above that does not pass updates through). Transitions requested during the
        /// pass are queued and drained at the end so the stack is never mutated mid-iteration.
        /// </summary>
        public void Update(float dt)
        {
            _updating = true;
            // Snapshot the live indices BEFORE the pass; deferred ops do not affect this frame's update set.
            for (int i = _scenes.Count - 1; i >= 0; i--)
            {
                _scenes[i].OnUpdate(dt);
                // Descend only if this scene lets updates pass through to the one below it.
                if (!_scenes[i].UpdateBelow) break;
            }
            _updating = false;

            while (_pending.Count > 0)
                _pending.Dequeue()();
        }

        /// <summary>Draw the visible scenes bottom-to-top (2D). No-op if empty. (For a 3D world pass, the
        /// <c>KhaozEngine.Game.Render3D</c> bridge adds a <c>Draw3D</c> extension over the same visible set.)</summary>
        public void Draw2D(SpriteBatch batch)
        {
            int from = FirstVisibleIndex();
            for (int i = from; i < _scenes.Count; i++)
                _scenes[i].OnDraw2D(batch);
        }

        /// <summary>Draw the visible scenes' point-space UI layer bottom-to-top (<see cref="GameScene.OnDrawUi"/>),
        /// in a separate pass after <see cref="Draw2D"/>. No-op if empty.</summary>
        public void DrawUi(SpriteBatch batch)
        {
            int from = FirstVisibleIndex();
            for (int i = from; i < _scenes.Count; i++)
                _scenes[i].OnDrawUi(batch);
        }

        /// <summary>Set <see cref="FrameWidth"/>/<see cref="FrameHeight"/> and forward <see cref="GameScene.OnResize"/> to every scene.</summary>
        public void Resize(int width, int height)
        {
            FrameWidth = width;
            FrameHeight = height;
            for (int i = 0; i < _scenes.Count; i++)
                _scenes[i].OnResize(width, height);
        }

        /// <summary>
        /// The bottom-most visible scene index: start at the top and descend while that scene draws what is below
        /// it and a scene exists below. Returns <see cref="Count"/> (a past-the-end index, so the draw loops are a
        /// no-op) when the stack is empty. Public so the <c>KhaozEngine.Game.Render3D</c> bridge's <c>Draw3D</c>
        /// extension can draw the same visible set, and so a headless test can verify visibility without a GPU.
        /// </summary>
        public int FirstVisibleIndex()
        {
            if (_scenes.Count == 0) return 0; // == Count; draw loops run zero iterations.
            int i = _scenes.Count - 1;
            while (i > 0 && _scenes[i].DrawBelow) i--;
            return i;
        }

        void ApplyPush(GameScene scene)
        {
            // A scene transition spends the in-flight pointer gesture: the press/release that triggered the
            // push must not also be honoured as a tap by a widget the new scene draws this same frame under the
            // same press-origin (the campaign-map "release auto-selects the difficulty" click-through).
            Pointer?.ConsumeGesture();
            scene.Manager = this;
            _scenes.Add(scene);
            scene.OnEnter();
        }

        void ApplyPop()
        {
            if (_scenes.Count == 0) return;
            Pointer?.ConsumeGesture();   // same rule on the way down (see ApplyPush).
            GameScene top = _scenes[_scenes.Count - 1];
            top.OnExit();
            _scenes.RemoveAt(_scenes.Count - 1);
            top.Manager = null;
        }

        void ApplyReplace(GameScene scene)
        {
            ApplyPop();
            ApplyPush(scene);
        }

        void ApplySwitchTo(GameScene scene)
        {
            ApplyClear();
            ApplyPush(scene);
        }

        void ApplyClear()
        {
            while (_scenes.Count > 0)
                ApplyPop();
        }
    }
}
