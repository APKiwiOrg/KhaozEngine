using System;
using Silk.NET.Windowing;   // WindowExtensions.Run(this IView), the blocking loop entry point below

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// The frame loop half of <see cref="AppWindow"/>: the two <c>Run</c> overloads and the pacing idle. It is here
    /// rather than in <c>AppWindow.cs</c> because that file is at its size ceiling, and because the loop is a
    /// distinct concern from window/device construction, sizing and input wiring. The per-frame phase ORDER itself
    /// lives in <see cref="FramePhases"/>, which is what makes it assertable without a window.
    /// </summary>
    public sealed partial class AppWindow
    {
        /// <summary>
        /// Opt in to renderer-owned swapchain initialization. Default false preserves the host's initial output
        /// bind and <see cref="ClearColor"/> clear. When true, the host omits both, allowing offscreen work before
        /// the first swapchain bind. The renderer must bind and initialize the full output in this frame's recording
        /// before any 2D draws or capture taps, including empty, loading and menu frames. Keep false for partial
        /// renderers or callbacks that rely on the host clear. This does not remove the renderer's final clear.
        /// </summary>
        public bool RendererInitializesSwapchain { get; set; }

        /// <summary>Run the frame loop until the window closes, calling <paramref name="onFrame"/> each frame. The loop
        /// is paced to the resolved <see cref="FrameCapHz"/> with a monotonic-clock limiter after present (independent
        /// of the swapchain's vsync). The <see cref="BackgroundThrottle"/> policy adjusts pacing when the window is
        /// backgrounded: an unfocused-but-visible window drops to a low cap, and a minimized window skips render +
        /// present entirely (<see cref="Frame.RenderSuppressed"/> is set) while still running <paramref name="onFrame"/>
        /// each idle tick so update-side simulation keeps advancing.</summary>
        public void Run(Action<Frame> onFrame) => Run(onFrame, null);

        /// <summary>
        /// Run the frame loop with a PRE-RECORD phase: <paramref name="onPrepare"/> is invoked each frame after the
        /// frame's dt / input / size are latched and BEFORE the frame's command list is opened, then
        /// <paramref name="onFrame"/> is invoked with that list recording. Everything the single-callback overload
        /// documents still holds, and passing <c>null</c> for <paramref name="onPrepare"/> is exactly that overload.
        /// <para>
        /// Reach for it when something in the frame has to submit GPU work on a command list of its OWN: opening one
        /// while the frame's list is recording is the nested recording the GPU seam refuses by name on every backend
        /// (see <see cref="FramePhases"/> and
        /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/429">#429</see>). <c>GameApp</c> uses this
        /// overload, so a game on <c>GameApp</c> / <c>GameApp3D</c> already has the phase and needs no change.
        /// </para>
        /// <para>
        /// <b>Do not record into <see cref="Frame.Commands"/> from <paramref name="onPrepare"/>.</b> The frame's list
        /// has not been begun yet during that callback (and on a render-suppressed frame it is never begun at all).
        /// Draws belong in <paramref name="onFrame"/>. The prepare phase is for work that owns its own list, plus the
        /// update-side work that has to precede the frame's queues being filled.
        /// </para>
        /// <para>
        /// Both callbacks run on a render-suppressed (minimized) frame, so simulation keeps advancing while iconified.
        /// </para>
        /// </summary>
        public void Run(Action<Frame> onFrame, Action<Frame>? onPrepare)
        {
            Show(); // ensure visible even if the host never called Show() (GameApp calls it after SetIcon). Idempotent.
            // Launch monitor, applied here rather than at construction so it lands AFTER the game's boot restore
            // (GameApp calls OnLoad, where a game re-applies its saved placement, immediately before this). Idempotent,
            // and a no-op unless an InitialMonitor or KE_WINDOW_MONITOR asked for one. See AppWindow.Launch.cs.
            ApplyLaunchPlacement();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var diagnostics = FrameDiagnosticsRecorder.FromEnvironment();

            // Composed ONCE, not per frame: the record-phase callback plus the rumble tick that has to follow it
            // (a Pulse issued during the frame must reach the motors on that frame). Building this inside the
            // per-frame lambda would allocate a closure every frame.
            Action<Frame> record = f => { onFrame(f); _rumble?.Tick(f.Dt); };

            _window.Render += dt =>
            {
                diagnostics?.Observe(dt, System.Diagnostics.Stopwatch.GetTimestamp(),
                    new FrameDiagnosticsWindowState(_accumulator.IsFocused, _minimized,
                        !_backgroundThrottle.Plan(new WindowActivity(_accumulator.IsFocused, _minimized), _effectiveBaseCapHz).RenderAndPresent,
                        _window.FramebufferSize.X, _window.FramebufferSize.Y, _effectiveBaseCapHz, _presentMode == PresentMode.Vsync,
                        RendererInitializesSwapchain, _frame.PreparePhaseSplitActive,
                        _frame.PreparePhaseSubmissionCount, _frame.PreparePhaseCompositeCount),
                    _device.Counters, _frame.CompletedShadowStageCallback, _frame.CompletedRecordingWaitCallback,
                    diagnostics.ReadRecordingWaitBoundary(_device as Gpu.IGpuRingWaitSource));
                _frame.BeginShadowStageCallback(diagnostics?.NextShadowStageRequest ?? default);
                _frame.BeginRecordingWaitCallback(diagnostics?.NextRecordingWaitRequest ?? default);
                float fdt = (float)Math.Min(dt, 0.1);
                // Capture first, so the snapshot reports what GLFW was told this frame. See AppWindow.PointerCapture.cs.
                ApplyPointerCapture();
                // The one composition seam: the built snapshot, then the optional filter, then the frame latches it.
                // Null filter is the raw snapshot with no allocation. See AppWindow.InputFilter.cs.
                InputState input = ApplyInputFilter(_inputFilter, BuildInput());
                int w = _window.FramebufferSize.X, h = _window.FramebufferSize.Y;

                // Background-throttle decision for this frame (pure). A minimized window skips render + present. An
                // unfocused-but-visible one still renders at a lowered cap. A focused window renders at the base cap.
                FramePlan plan = _backgroundThrottle.Plan(new WindowActivity(_accumulator.IsFocused, _minimized), _effectiveBaseCapHz);
                bool render = plan.RenderAndPresent;

                _frame.Dt = fdt; _frame.Input = input;
                // Exact OS content scale, re-read each frame so a monitor move or OS scale change lands here.
                _frame.SetMetrics(w, h, _window.Size.X, _window.Size.Y, WindowContentScale());
                _frame.Commands = _cl;
                _frame.RenderSuppressed = !render;

                // The frame's whole shape: prepare -> open + optional host clear -> record -> capture taps -> end +
                // submit -> taps' after-submit -> present. Both callbacks always run, so update advances even on a
                // render-suppressed frame. The taps get the unclamped delta (see AppWindow.CaptureTaps.cs).
                FramePhases.Run(_frame, render, _device, _cl, ClearColor, onPrepare, record, _captureTaps,
                    dt * 1000.0, RendererInitializesSwapchain);

                // Pace the loop to the plan's cap. Silk's own loop runs the callback as fast as the GPU allows (a
                // Metal present does not throttle the CPU), so idle here to hold the target cadence - the base
                // cap when focused, a low cap when unfocused, an idle rate when minimized. Rebuild the limiter only when
                // the target Hz changes (a focus / minimize transition), so steady-state pacing keeps a stable anchor.
                if (plan.CapHz != _paceHz) { _paceHz = plan.CapHz; _paceLimiter = new FrameLimiter(plan.CapHz); }
                if (_paceLimiter.Enabled)
                {
                    double wait = _paceLimiter.WaitBeforeNext(clock.Elapsed.TotalSeconds);
                    if (wait > 0) PreciseIdle(clock, wait);
                }

                if (_maxFrames > 0 && ++_frameCount >= _maxFrames) _window.Close();
                _frame.CompleteShadowStageCallback();
                _frame.CompleteRecordingWaitCallback();
            };
            _window.Run();
        }

        /// <summary>Idle for <paramref name="seconds"/> using the monotonic <paramref name="clock"/>: sleep the bulk
        /// (leaving a ~1 ms margin so the OS timer granularity can't overshoot the cap), then spin the remainder.</summary>
        static void PreciseIdle(System.Diagnostics.Stopwatch clock, double seconds)
        {
            double deadline = clock.Elapsed.TotalSeconds + seconds;
            int bulkMs = (int)(seconds * 1000.0) - 1;
            if (bulkMs > 0) System.Threading.Thread.Sleep(bulkMs);
            while (clock.Elapsed.TotalSeconds < deadline) System.Threading.Thread.SpinWait(64);
        }
    }
}
