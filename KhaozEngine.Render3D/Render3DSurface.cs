using System;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A 3D scene bound to an <see cref="AppWindow"/>: builds a <see cref="Scene3D"/> on the window's GPU device
    /// and renders it into the window's frames, so a Render2D HUD can draw on top. The window owns the device
    /// and the frame loop; this records into the frame's command list. Composition order, per frame:
    /// <see cref="Scene3D.Begin"/> + submit instances + <see cref="Scene3D.PrepareFrame"/> in the window's pre-record
    /// phase, then <see cref="Render"/> in the record phase, then the 2D surface draws the HUD over it. A host that
    /// does not use the pre-record phase still gets a correct frame: <see cref="Render"/> prepares if nobody did.
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
        /// On a windowed host that call is a SAFETY NET, not the effective one, and it is meant to be. The frame's
        /// command list is already recording by the time this runs, so a producer preparing here would still nest a
        /// list inside it. The fix is upstream, in the loop: <see cref="AppWindow.Run(Action{Frame}, Action{Frame})"/>
        /// takes a pre-record callback, and <c>GameApp3D</c> runs <c>Scene.Begin()</c> -> its draws ->
        /// <see cref="Scene3D.PrepareFrame"/> there, so by the time this method runs the frame is already prepared and
        /// the call below no-ops (#429). A host driving a surface off a raw <see cref="AppWindow"/> should do the
        /// same: queue and prepare the scene in the <c>onPrepare</c> callback, and call this from <c>onFrame</c>.
        /// <b>A host that does not still nests</b>, because the call below then runs inside the frame's recording.
        /// That residual is a refusal on every backend: a producer with GPU work of its own opens its list through
        /// <see cref="Gpu.GpuRecording"/>, which sees the frame's own list already open and throws
        /// <see cref="Gpu.GpuNestedRecordingException"/> naming the fix. A loud, portable, same-everywhere refusal
        /// instead of a picture that is silently right on the dev machine and corrupt on a player's is the intended
        /// trade (#424).
        /// <b>That refusal is now the whole of it</b>, decided rather than inherited (#690): the vendored Veldrid
        /// fork's second-recorder guardrail used to catch its own leg one layer below this, and both the fork and
        /// the leg were deleted in 18.0.0. Nothing sits under the seam any more, and nothing needs to: the register
        /// refuses before a list is begun, so the host sees the same exception naming the same fix wherever it runs.
        /// </para></summary>
        public void Render(Frame frame)
        {
            Scene.PrepareFrame();
            Scene.RenderInternal(frame.Commands, frame.Width, frame.Height, _window.GpuDevice.SwapchainFramebuffer!);
        }

        public void Dispose() => Scene.Dispose();
    }
}
