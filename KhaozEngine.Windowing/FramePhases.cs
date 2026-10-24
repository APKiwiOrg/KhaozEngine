using System;
using System.Collections.Immutable;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// The order of one windowed frame: PREPARE, with no command list open, then RECORD, inside the frame's list.
    /// <see cref="AppWindow.Run(Action{Frame}, Action{Frame})"/> does nothing else per frame, so this type IS the
    /// loop's phase contract rather than a description of it.
    /// <para>Preparation may grow resources or run producers that submit their own command lists. It runs before
    /// the host claims the device for recording. The record callback owns the one open list on that device, so
    /// an unprepared subsystem must refuse before changing storage or attempting a nested recording.
    /// A prepared scene can repeat its preparation call as a no-op inside the record callback.</para>
    /// </summary>
    static class FramePhases
    {
        /// <summary>
        /// Run one frame's two phases against <paramref name="commands"/>. <paramref name="onPrepare"/> is invoked
        /// first, with nothing recording, so a callback may create, submit and drain command lists of its own.
        /// <paramref name="onFrame"/> is invoked with the frame's list open, bound to the swapchain and cleared to
        /// <paramref name="clearColor"/> (unless <paramref name="render"/> is false, when no list is opened and
        /// nothing is presented, and both callbacks still run so update-side work keeps advancing).
        /// </summary>
        internal static void Run(Frame frame, bool render, IGpuDevice device, IGpuCommandList commands,
            Color clearColor, Action<Frame>? onPrepare, Action<Frame> onFrame)
            => Run(frame, render, device, commands, clearColor, onPrepare, onFrame, null, 0);

        /// <summary>
        /// The same frame with the window's capture taps. A null registry, or one with nothing to do, runs the
        /// frame exactly as the overload without taps: one submit and one present. Otherwise every tap records
        /// after <paramref name="onFrame"/> inside the frame's list, the list is submitted with the fence a tap
        /// handed in (or without one), every tap's after-submit callback runs with nothing recording, and then
        /// the frame presents. A render-suppressed frame runs no tap. <c>frameMilliseconds</c> is the loop's
        /// unclamped frame delta, handed to the taps.
        /// </summary>
        internal static void Run(Frame frame, bool render, IGpuDevice device, IGpuCommandList commands,
            Color clearColor, Action<Frame>? onPrepare, Action<Frame> onFrame, FrameCaptureTaps? taps,
            double frameMilliseconds)
            => Run(frame, render, device, commands, clearColor, onPrepare, onFrame, taps, frameMilliseconds,
                rendererInitializesSwapchain: false);

        /// <summary>
        /// The window's explicit renderer-owned initialization policy. When enabled, the record callback must bind
        /// and initialize the entire swapchain output before any 2D draws or capture taps. Recording, submission,
        /// suppression and exception handling follow the legacy overloads unchanged.
        /// </summary>
        internal static void Run(Frame frame, bool render, IGpuDevice device, IGpuCommandList commands,
            Color clearColor, Action<Frame>? onPrepare, Action<Frame> onFrame, FrameCaptureTaps? taps,
            double frameMilliseconds, bool rendererInitializesSwapchain)
        {
            bool capture = taps is { HasWork: true };
            // Prepare. The frame's dt / input / size are already latched onto `frame`, so a callback sees this
            // frame's state, not the previous one's - which is what lets the world's queues be filled here rather
            // than in the record phase. frame.Commands is NOT recording during this call.
            frame.BeginSubmission(device);
            try
            {
                onPrepare?.Invoke(frame);
                IGpuFence? fence = null;

                // Record. Everything from here to the scope's close belongs in the frame's own list, and the scope is
                // what makes that enforceable rather than advisory: it claims the device in the seam's open-recording
                // register, so any engine API that opens a list of its own from inside onFrame refuses by name instead
                // of resetting this recording's device state (#424). A frame that renders nothing holds the default
                // not-recording scope, claims nothing, and leaves the prepare-phase freedom intact.
                GpuRecordingScope recording = render
                    ? GpuRecording.Open(device, commands, "the window's frame list")
                    : default;
                Exception? frameError = null;
                try
                {
                    if (render && !rendererInitializesSwapchain)
                    {
                        commands.SetFramebuffer(device.SwapchainFramebuffer!);
                        commands.ClearColorTarget(0, clearColor);
                    }
                    onFrame(frame);
                    // A tap's copy stays after the game's draws in the same recording and submission.
                    if (render && capture) fence = taps!.Record(frame, commands, frameMilliseconds);
                }
                catch (Exception error) { frameError = error; throw; }
                finally
                {
                    try { recording.Dispose(); }
                    catch (Exception finalization) when (frameError is not null)
                    { throw Transport(frameError, finalization, "renderer.frame.finalize"); }
                }

                if (render)
                {
                    if (fence is null) frame.Submission.Submit(commands);
                    else frame.Submission.Submit(commands, [fence]);
                    if (capture) taps!.AfterSubmit();
                    device.Present();
                }
                else frame.Submission.Abandon();
            }
            catch (Exception error)
            {
                try { frame.Submission.Abandon(); }
                catch (Exception recovery) { throw Transport(error, recovery, "renderer.frame.abandon"); }
                throw;
            }
        }

        static GpuDiagnosticFailureException Transport(Exception original, Exception cleanup, string stage)
        {
            var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
            if (original is GpuDiagnosticFailureException first) failures.AddRange(first.Failures);
            else failures.Add(new("renderer.frame", original));
            if (cleanup is GpuDiagnosticFailureException later) failures.AddRange(later.Failures);
            else failures.Add(new(stage, cleanup));
            return new(failures.ToImmutable());
        }

    }
}
