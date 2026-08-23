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
            var clock = System.Diagnostics.Stopwatch.StartNew();

            // Composed ONCE, not per frame: the record-phase callback plus the rumble tick that has to follow it
            // (a Pulse issued during the frame must reach the motors on that frame). Building this inside the
            // per-frame lambda would allocate a closure every frame.
            Action<Frame> record = f => { onFrame(f); _rumble?.Tick(f.Dt); };

            _window.Render += dt =>
            {
                float fdt = (float)Math.Min(dt, 0.1);
                InputState input = BuildInput();
                int w = _window.FramebufferSize.X, h = _window.FramebufferSize.Y;

                // Background-throttle decision for this frame (pure). A minimized window skips render + present. An
                // unfocused-but-visible one still renders at a lowered cap. A focused window renders at the base cap.
                FramePlan plan = _backgroundThrottle.Plan(new WindowActivity(_accumulator.IsFocused, _minimized), _effectiveBaseCapHz);
                bool render = plan.RenderAndPresent;

                _frame.Dt = fdt; _frame.Input = input; _frame.Width = w; _frame.Height = h;
                _frame.LogicalWidth = _window.Size.X; _frame.LogicalHeight = _window.Size.Y;
                _frame.Commands = _cl;
                _frame.RenderSuppressed = !render;

                // The frame's whole shape: prepare (nothing open) -> open + clear -> record -> end + submit +
                // present. Both callbacks always run, so update advances even on a render-suppressed frame.
                FramePhases.Run(_frame, render, _device, _cl, ClearColor, onPrepare, record);

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
