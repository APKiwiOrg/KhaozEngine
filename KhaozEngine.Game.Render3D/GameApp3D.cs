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

        /// <summary>Drives the 3D pass each frame before the 2D batch.</summary>
        protected override void OnRenderWorld(Frame frame)
        {
            _surface3D.Scene.Begin();
            OnDraw3D(_surface3D.Scene);
            _surface3D.Render(frame);
        }

        /// <summary>Dispose the 3D surface before the base tears down the 2D surface + window.</summary>
        protected override void OnDispose() => _surface3D.Dispose();
    }
}
