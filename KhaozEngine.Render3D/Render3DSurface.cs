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
        readonly PreparePhaseSceneSubmission? _prepareSubmission;

        public Scene3D Scene { get; }

        /// <summary>Build the 3D surface on the window's GPU device. <paramref name="shadows"/> (optional) seeds the
        /// scene's construction-time shadow settings (issue #27): the atlas resolution / cascade count / step-blend
        /// provisioning are read ONCE as the scene allocates its atlas, so they must be supplied here rather than set on
        /// <c>Scene.Post.Quality.Shadows</c> afterwards (which throws for those knobs). All other shadow tuning stays
        /// runtime-mutable on <c>Scene.Post</c>.</summary>
        public Render3DSurface(AppWindow window, ShadowSettings? shadows = null)
        {
            bool split = PreparePhaseSceneSubmission.Parse(Environment.GetEnvironmentVariable("KE_PREPARE_PHASE_SPLIT"));
            if (split && (!window.RendererInitializesSwapchain || window.GpuDevice.Backend != Gpu.GpuBackendKind.MetalNative))
                throw new InvalidOperationException("KE_PREPARE_PHASE_SPLIT requires MetalNative and renderer-owned swapchain initialization.");
            _window = window;
            Scene = new Scene3D(window.GpuDevice, window.GpuDevice.SwapchainFramebuffer!.Outputs, shadows);
            if (split) _prepareSubmission = new PreparePhaseSceneSubmission(window.GpuDevice, Scene);
        }

        /// <summary>Whether this surface uses the private prepare-phase submission experiment.</summary>
        public bool PreparePhaseSplitEnabled => _prepareSubmission is not null;

        /// <summary>Submit the private scene prefix after queue preparation and before the frame recording opens.</summary>
        public void PrepareRender(Frame frame)
        {
            frame.DeclareRecordingWaitPrefix(_prepareSubmission is not null && !frame.RenderSuppressed);
            try { _prepareSubmission?.Prepare(frame); }
            catch { Scene.CancelShadowStageProbe(); throw; }
        }

        /// <summary>Record the queued scene into the window framebuffer. The frame command list is already
        /// recording here. Queue draws and call <see cref="Scene3D.PrepareFrame(int, int)"/> with the frame's
        /// dimensions in AppWindow's pre-record callback. GameApp3D forwards those dimensions automatically.
        /// Bound optical water refuses missing preparation or changed viewport inputs before replacing targets.
        /// The no-argument call below is an idempotent legacy safety net. It cannot prepare bound resources here.
        /// Legacy FFT priming from this phase is refused by <see cref="Gpu.GpuRecording"/> as a nested recording.</summary>
        public void Render(Frame frame)
            => RecordSceneOutput(Scene, frame, _prepareSubmission, _window.GpuDevice.SwapchainFramebuffer!);

        // Shared by the real surface and headless lifetime/native fixtures.
        internal static void RecordSceneOutput(Scene3D scene, Frame frame, PreparePhaseSceneSubmission? prepared,
            Gpu.IGpuFramebuffer target)
        {
            try
            {
                if (prepared is not null) prepared.Render(frame, target);
                else
                {
                    scene.PrepareFrame();
                    scene.RenderInternal(frame.Commands, frame.Width, frame.Height, target);
                }
                scene.PublishShadowStageProbe(frame);
            }
            catch { scene.CancelShadowStageProbe(); throw; }
        }

        public void Dispose() => DisposeSceneAndPrefix(Scene, _prepareSubmission);

        internal static void DisposeSceneAndPrefix(Scene3D scene, IDisposable? prefix)
        {
            try { scene.Dispose(); }
            finally { if (scene.DisposalSafeForDependants) prefix?.Dispose(); }
        }
    }
}
