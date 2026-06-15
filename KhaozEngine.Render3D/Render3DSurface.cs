using System;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A 3D scene bound to an <see cref="AppWindow"/>: builds a <see cref="Scene3D"/> on the window's GPU device
    /// and renders it into the window's frames, so a Render2D HUD can draw on top. The window owns the device
    /// and the frame loop; this records into the frame's command list. Composition order, per frame:
    /// <see cref="Scene3D.Begin"/> + submit instances, <see cref="Render"/> (3D fills the frame), then the 2D
    /// surface draws the HUD over it.
    /// </summary>
    public sealed class Render3DSurface : IDisposable
    {
        readonly AppWindow _window;

        public Scene3D Scene { get; }

        public Render3DSurface(AppWindow window)
        {
            _window = window;
            Scene = new Scene3D(window.Device, window.MainSwapchain.Framebuffer.OutputDescription);
        }

        /// <summary>Record the queued 3D scene into this frame's command list, ending on the window framebuffer.</summary>
        public void Render(Frame frame) =>
            Scene.RenderInternal(frame.Commands, frame.Width, frame.Height, _window.MainSwapchain.Framebuffer);

        public void Dispose() => Scene.Dispose();
    }
}
