using System;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game
{
    /// <summary>
    /// A <see cref="GameApp"/> that also stands up a 3D scene: it builds a <see cref="Render3DSurface"/> bound to
    /// the window, and drives the 3D pass across the frame's two phases - <c>Scene.Begin()</c> ->
    /// <see cref="OnDraw3D"/> -> <c>Scene.PrepareFrame()</c> in <see cref="GameApp.OnPrepareWorld"/>, then the
    /// compose in <see cref="GameApp.OnRenderWorld"/>, before the 2D HUD pass. A 3D game subclasses this instead of
    /// <see cref="GameApp"/> and overrides <see cref="OnDraw3D"/>; a 2D game uses <see cref="GameApp"/> and pulls
    /// no 3D renderer.
    /// </summary>
    public abstract class GameApp3D : GameApp
    {
        readonly Render3DSurface _surface3D;

        // OnDraw3D as a delegate, built ONCE here rather than per frame: PrepareScene takes the queue fill as a
        // parameter so the Begin -> queue -> prepare order is a named, headless-testable unit instead of three
        // statements nobody can assert on without a window.
        readonly Action<Scene3D> _drawWorld;

        /// <summary>Stand up the app and its 3D surface. A 3D game passes <paramref name="initialShadows"/> to size the
        /// shadow atlas at construction (resolution / cascade count / step-blend provisioning are construction-time
        /// knobs, issue #27): e.g. <c>base(options, new ShadowSettings { Mode = ShadowMode.ShadowMap, ShadowCascadeCount
        /// = 4 })</c>. All other shadow tuning stays runtime-mutable on <see cref="Scene"/>.Post.</summary>
        protected GameApp3D(in GameAppOptions options, ShadowSettings? initialShadows = null) : base(options)
        {
            _surface3D = new Render3DSurface(Window, initialShadows);
            _drawWorld = OnDraw3D;
        }

        /// <summary>The 3D surface bound to the window.</summary>
        protected Render3DSurface Surface3D => _surface3D;
        /// <summary>The 3D scene (<see cref="Surface3D"/>.Scene).</summary>
        protected Scene3D Scene => _surface3D.Scene;

        /// <summary>Submit 3D instances. <see cref="Scene"/>'s <c>Begin()</c> is already called when this runs. Runs in
        /// the frame's PRE-RECORD phase (after <c>OnUpdate</c>, before the frame's command list opens), so it sees this
        /// frame's simulation state and may still open a command list of its own.</summary>
        protected virtual void OnDraw3D(Scene3D scene) { }

        /// <summary>A 3D app feeds the HUD a per-pass CPU-encode timing section.</summary>
        protected override bool SupportsPassTimings => true;

        /// <summary>The whole-frame draw stats: the base 2D batch total plus this scene's <see cref="Scene3D.LastFrameStats"/>.</summary>
        protected override RenderFrameStats CollectFrameStats() => base.CollectFrameStats() + _surface3D.Scene.LastFrameStats;

        /// <summary>The <see cref="Scene3D.EnableTiming"/> decision <see cref="OnPrepareWorld"/> drives from the
        /// built-in overlay: <c>hud.Visible</c> when the overlay exists, or null ("leave the flag alone") when it
        /// does not, so a consumer that opted out via <see cref="GameAppOptions.DisableDiagnosticsOverlay"/> keeps
        /// whatever it set on <see cref="Scene3D.EnableTiming"/> itself instead of it being forced false every frame
        /// (issue #404). Pure and headless-testable, mirroring <c>GameApp.ShouldRaiseResume</c>: like that helper,
        /// it exists because <see cref="OnPrepareWorld"/> itself needs a real window (GameApp.Run's convention), so
        /// only the decision is unit-tested, not the frame loop.</summary>
        internal static bool? DesiredEnableTiming(DiagnosticsHud? hud) => hud?.Visible;

        /// <summary>
        /// The scene half of the frame's PRE-RECORD phase: begin the scene, let the game queue its draws, then run
        /// <see cref="Scene3D.PrepareFrame"/> - all while the window's frame command list is still closed, which is
        /// the whole point (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/429">#429</see>). The scene's
        /// per-pass timing flag is decided here too, so <see cref="OnDraw3D"/> and the passes it feeds see one value
        /// for the frame.
        /// </summary>
        protected override void OnPrepareWorld(Frame frame)
        {
            DiagnosticsHud? hud = Diagnostics;
            if (DesiredEnableTiming(hud) is { } enableTiming) _surface3D.Scene.EnableTiming = enableTiming;

            PrepareScene(_surface3D.Scene, _drawWorld);
        }

        /// <summary>
        /// The frame's scene contract in one place: <see cref="Scene3D.Begin"/>, then the queue fill, then
        /// <see cref="Scene3D.PrepareFrame"/>. It runs where no command list is open, so a producer that must submit
        /// and drain a list of its own (the FFT ocean's priming pass) does it here rather than nested inside the
        /// frame's recording, which is a device fault on Direct3D11 in immediate-context mode (#423).
        /// <para>
        /// Static and scene-parameterized so the ORDER is assertable headless, the way
        /// <see cref="DesiredEnableTiming"/> makes the timing decision assertable: the loop around it still needs a
        /// real window.
        /// </para>
        /// </summary>
        internal static void PrepareScene(Scene3D scene, Action<Scene3D> drawWorld)
        {
            scene.Begin();
            drawWorld(scene);
            scene.PrepareFrame();
        }

        /// <summary>Records the 3D pass each frame before the 2D batch, into the frame's command list. The scene was
        /// begun, queued and prepared in <see cref="OnPrepareWorld"/>, so <see cref="Render3DSurface.Render"/>'s own
        /// <see cref="Scene3D.PrepareFrame"/> call finds the frame already prepared and no-ops. Couples the scene's
        /// per-pass timing to the HUD: the resulting per-pass milliseconds are fed into the HUD's rolling meter after
        /// the render (the flag itself was set in the prepare phase).</summary>
        protected override void OnRenderWorld(Frame frame)
        {
            DiagnosticsHud? hud = Diagnostics;
            _surface3D.Render(frame);

            if (_surface3D.Scene.EnableTiming && hud?.PassTimings is { } pt)
            {
                Scene3DPassTimingsMs t = _surface3D.Scene.PassTimingsMs;
                pt.Sample("shadow", t.ShadowDepthMs);
                pt.Sample("model", t.ModelMs);
                pt.Sample("transparents", t.TransparentsMs);
                pt.Sample("post", t.PostMs);
            }
        }

        /// <summary>Dispose the 3D surface before the base tears down the 2D surface + window.</summary>
        protected override void OnDispose() => _surface3D.Dispose();
    }
}
