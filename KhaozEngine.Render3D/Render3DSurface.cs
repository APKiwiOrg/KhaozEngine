using System;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A 3D scene bound to an <see cref="AppWindow"/>: builds a <see cref="Scene3D"/> on the window's GPU device
    /// and renders it into the window's frames, so a Render2D HUD can draw on top. The window owns the device
    /// and the frame loop; this records into the frame's command list. Composition order, per frame:
    /// <see cref="Scene3D.Begin"/> + submit instances + <see cref="Scene3D.PrepareFrame()"/> in the window's pre-record
    /// phase, then <see cref="Render"/> in the record phase, then the 2D surface draws the HUD over it. Prepare the scene in the pre-record callback. An unprepared scene refuses inside the frame recording.
    /// </summary>
    public sealed class Render3DSurface : IDisposable
    {
        readonly AppWindow _window;

        public Scene3D Scene { get; }

        /// <summary>Prepares the queued scene using the latched display size, before the frame list opens.</summary>
        public void Prepare(Frame frame) => Scene.PrepareFrame(frame.Width, frame.Height);

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

        /// <summary>Record the queued scene into this frame's list, ending on the window framebuffer.
        /// The host prepares the scene in its pre-record callback. The repeated <see cref="Scene3D.PrepareFrame()"/>
        /// call is then a no-op. A first preparation inside an open list refuses before resource or state changes.
        /// Aggregate coverage copies share the frame's submission owner and command list.</summary>
        public void Render(Frame frame)
        {
            Scene.PrepareFrame();
            Scene.RenderInternal(frame.Commands, frame.Width, frame.Height, _window.GpuDevice.SwapchainFramebuffer!);
            Scene.RecordCoverageReadbacks(frame.Commands, frame.Submission);
        }

        public void Dispose() => Scene.Dispose();
    }
}
