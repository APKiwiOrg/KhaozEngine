using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game
{
    /// <summary>
    /// A <see cref="GameApp"/> that also stands up a 3D scene: it builds a <see cref="Render3DSurface"/> bound to
    /// the window, and drives the 3D pass (<c>Scene.Begin()</c> -> <see cref="OnDraw3D"/> -> compose) in the
    /// <see cref="GameApp.OnRenderWorld"/> seam, before the 2D HUD pass. A 3D game subclasses this instead of
    /// <see cref="GameApp"/> and overrides <see cref="OnDraw3D"/>; a 2D game uses <see cref="GameApp"/> and pulls
    /// no 3D renderer.
    /// </summary>
    public abstract class GameApp3D : GameApp
    {
        readonly Render3DSurface _surface3D;

        /// <summary>Stand up the app and its 3D surface. A 3D game passes <paramref name="initialShadows"/> to size the
        /// shadow atlas at construction (resolution / cascade count / step-blend provisioning are construction-time
        /// knobs, issue #27): e.g. <c>base(options, new ShadowSettings { Mode = ShadowMode.ShadowMap, ShadowCascadeCount
        /// = 4 })</c>. All other shadow tuning stays runtime-mutable on <see cref="Scene"/>.Post.</summary>
        protected GameApp3D(in GameAppOptions options, ShadowSettings? initialShadows = null) : base(options)
        {
            _surface3D = new Render3DSurface(Window, initialShadows);
        }

        /// <summary>The 3D surface bound to the window.</summary>
        protected Render3DSurface Surface3D => _surface3D;
        /// <summary>The 3D scene (<see cref="Surface3D"/>.Scene).</summary>
        protected Scene3D Scene => _surface3D.Scene;

        /// <summary>Submit 3D instances; <see cref="Scene"/>'s <c>Begin()</c> is already called when this runs.</summary>
        protected virtual void OnDraw3D(Scene3D scene) { }

        /// <summary>A 3D app feeds the HUD a per-pass CPU-encode timing section.</summary>
        protected override bool SupportsPassTimings => true;

        /// <summary>The whole-frame draw stats: the base 2D batch total plus this scene's <see cref="Scene3D.LastFrameStats"/>.</summary>
        protected override RenderFrameStats CollectFrameStats() => base.CollectFrameStats() + _surface3D.Scene.LastFrameStats;

        /// <summary>The <see cref="Scene3D.EnableTiming"/> decision <see cref="OnRenderWorld"/> drives from the
        /// built-in overlay: <c>hud.Visible</c> when the overlay exists, or null ("leave the flag alone") when it
        /// does not, so a consumer that opted out via <see cref="GameAppOptions.DisableDiagnosticsOverlay"/> keeps
        /// whatever it set on <see cref="Scene3D.EnableTiming"/> itself instead of it being forced false every frame
        /// (issue #404). Pure and headless-testable, mirroring <c>GameApp.ShouldRaiseResume</c>: like that helper,
        /// it exists because <see cref="OnRenderWorld"/> itself needs a real window (GameApp.Run's convention), so
        /// only the decision is unit-tested, not the render loop.</summary>
        internal static bool? DesiredEnableTiming(DiagnosticsHud? hud) => hud?.Visible;

        /// <summary>Drives the 3D pass each frame before the 2D batch. Couples the scene's per-pass timing to the HUD:
        /// timing is enabled ONLY while the overlay is visible (so it costs nothing when hidden), and the resulting
        /// per-pass milliseconds are fed into the HUD's rolling meter after the render.</summary>
        protected override void OnRenderWorld(Frame frame)
        {
            DiagnosticsHud? hud = Diagnostics;
            if (DesiredEnableTiming(hud) is { } enableTiming) _surface3D.Scene.EnableTiming = enableTiming;

            _surface3D.Scene.Begin();
            OnDraw3D(_surface3D.Scene);
            // The frame's queues are full, so the producers that submit GPU work of their own run now, before the
            // scene is recorded (Scene3D.PrepareFrame). CAVEAT, and it is the windowed loop's rather than this
            // seam's: AppWindow.Run opens the frame's command list BEFORE it calls back into the app, so this runs
            // at the right point in the frame's logic but with that list already recording. On Direct3D11 in
            // immediate-context mode the ocean prime therefore still nests here, exactly as it did before #423,
            // until the frame loop grows a pre-record phase:
            // https://github.com/APKiwiOrg/KhaozEngine/issues/429
            _surface3D.Scene.PrepareFrame();
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
