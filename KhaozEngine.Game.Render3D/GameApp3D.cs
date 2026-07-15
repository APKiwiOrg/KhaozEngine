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

        protected GameApp3D(in GameAppOptions options) : base(options)
        {
            _surface3D = new Render3DSurface(Window);
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

        /// <summary>Drives the 3D pass each frame before the 2D batch. Couples the scene's per-pass timing to the HUD:
        /// timing is enabled ONLY while the overlay is visible (so it costs nothing when hidden), and the resulting
        /// per-pass milliseconds are fed into the HUD's rolling meter after the render.</summary>
        protected override void OnRenderWorld(Frame frame)
        {
            DiagnosticsHud? hud = Diagnostics;
            _surface3D.Scene.EnableTiming = hud is { Visible: true };

            _surface3D.Scene.Begin();
            OnDraw3D(_surface3D.Scene);
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
