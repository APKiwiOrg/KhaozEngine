using System;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A 3D scene bound to an <see cref="AppWindow"/>: builds a <see cref="Scene3D"/> on the window's GPU device
    /// and renders it into the window's frames, so a Render2D HUD can draw on top. The window owns the device
    /// and the frame loop; this records into the frame's command list. Composition order, per frame:
    /// <see cref="Scene3D.Begin"/> + submit instances + <see cref="Scene3D.PrepareFrame(int, int)"/> with the frame's
    /// size in the window's pre-record phase, then <see cref="Render"/> in the record phase, then the 2D surface
    /// draws the HUD over it. Bound water cannot prepare resources from the recording phase.
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

        /// <summary>Record the queued scene into the window framebuffer. The frame command list is already
        /// recording here. Queue draws and call <see cref="Scene3D.PrepareFrame(int, int)"/> with the frame's
        /// dimensions in AppWindow's pre-record callback. GameApp3D forwards those dimensions automatically.
        /// Bound optical water refuses missing preparation or changed viewport inputs before replacing targets.
        /// The no-argument call below is an idempotent legacy safety net. It cannot prepare bound resources here.
        /// Legacy FFT priming from this phase is refused by <see cref="Gpu.GpuRecording"/> as a nested recording.</summary>
        public void Render(Frame frame)
        {
            Scene.PrepareFrame();
            Scene.RenderInternal(frame.Commands, frame.Width, frame.Height, _window.GpuDevice.SwapchainFramebuffer!);
        }

        public void Dispose() => Scene.Dispose();
    }
}
