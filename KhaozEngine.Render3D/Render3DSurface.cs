using System;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A 3D scene bound to an <see cref="AppWindow"/>: builds a <see cref="Scene3D"/> on the window's GPU device
    /// and renders it into the window's frames, so a Render2D HUD can draw on top. The window owns the device
    /// and the frame loop; this records into the frame's command list. Composition order, per frame:
    /// <see cref="Scene3D.Begin"/> + submit instances, <see cref="Render"/> (which runs
    /// <see cref="Scene3D.PrepareFrame"/> for you, then fills the frame), then the 2D surface draws the HUD over it.
    /// </summary>
    public sealed class Render3DSurface : IDisposable
    {
        readonly AppWindow _window;

        public Scene3D Scene { get; }

        /// <summary>Build the 3D surface on the window's GPU device. <paramref name="shadows"/> (optional) seeds the
        /// scene's construction-time shadow settings (issue #27): the atlas resolution / cascade count / step-blend
        /// provisioning are read ONCE as the scene allocates its atlas, so they must be supplied here rather than set on
        /// <c>Scene.Post.Quality.Shadows</c> afterwards (which throws for those knobs). All other shadow tuning stays
        /// runtime-mutable on <c>Scene.Post</c>.</summary>
        public Render3DSurface(AppWindow window, ShadowSettings? shadows = null)
        {
            _window = window;
            Scene = new Scene3D(window.GpuDevice, window.GpuDevice.SwapchainFramebuffer!.Outputs, shadows);
        }

        /// <summary>Record the queued 3D scene into this frame's command list, ending on the window framebuffer.
        /// Runs <see cref="Scene3D.PrepareFrame"/> first, so a consumer building a surface directly gets the frame's
        /// pre-recording phase without having to know it exists (the queues are full by now and the scene is about
        /// to be recorded, which is exactly where it belongs).
        /// <para>
        /// One caveat, and it is the windowed frame loop's rather than this class's: <see cref="AppWindow.Run"/>
        /// opens the frame's command list BEFORE calling back into the app, so this prepare runs at the right point
        /// in the frame's logic with that list already recording. A producer that submits its own list therefore
        /// still nests inside it on Direct3D11 in immediate-context mode, exactly as it did before, until the frame
        /// loop grows a pre-record phase: https://github.com/APKiwiOrg/KhaozEngine/issues/429
        /// </para></summary>
        public void Render(Frame frame)
        {
            Scene.PrepareFrame();
            Scene.RenderInternal(frame.Commands, frame.Width, frame.Height, _window.GpuDevice.SwapchainFramebuffer!);
        }

        public void Dispose() => Scene.Dispose();
    }
}
