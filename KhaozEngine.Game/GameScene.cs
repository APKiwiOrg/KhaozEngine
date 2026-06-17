using KhaozEngine.Render2D;

namespace KhaozEngine.Game
{
    /// <summary>
    /// A full game state on the scene stack: it owns its own per-frame update, 3D submission, 2D HUD draw,
    /// and enter/exit lifecycle. Pushed onto a <see cref="SceneManager"/>, which runs a stack of scenes with
    /// overlay support (a transparent pause scene over a frozen gameplay scene), deferred transitions, and
    /// lifecycle. This is DISTINCT from the Gui <c>ScreenStack</c>/<c>Screen</c> (a 2D-UI-only stack): a
    /// <see cref="GameScene"/> covers the whole frame (3D world + 2D HUD) and a scene may itself drive a Gui
    /// <c>ScreenStack</c> internally for its menus.
    /// </summary>
    public abstract class GameScene
    {
        /// <summary>
        /// Set by the manager when the scene is pushed; null before it is pushed and after it is popped. Scenes
        /// read the shared per-frame context (Input/Pointer/Viewport/FrameWidth/FrameHeight) and drive
        /// transitions (Push/Pop/Replace/SwitchTo) through it.
        /// </summary>
        public SceneManager? Manager { get; internal set; }

        /// <summary>
        /// When true, the scene directly below this one is also DRAWN, so this scene is a transparent overlay
        /// (e.g. a pause menu over the frozen game). Default false (opaque: covers what is below).
        /// </summary>
        public bool DrawBelow;

        /// <summary>
        /// When true, the scene directly below this one also UPDATES (rare; default false, so an overlay freezes
        /// the scene under it).
        /// </summary>
        public bool UpdateBelow;

        /// <summary>Called when the scene is pushed onto the stack.</summary>
        public virtual void OnEnter() { }

        /// <summary>Called when the scene is removed from the stack.</summary>
        public virtual void OnExit() { }

        /// <summary>Per-frame simulation step. Only called when this scene is "live" (see manager update gating).</summary>
        public virtual void OnUpdate(float dt) { }

        /// <summary>Draw the HUD / 2D UI. Only called when the scene is visible.</summary>
        public virtual void OnDraw2D(SpriteBatch batch) { }

        /// <summary>The frame size changed. Forwarded to every scene on the stack.</summary>
        public virtual void OnResize(int width, int height) { }
    }
}
