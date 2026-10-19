using System;
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
        {
            // Prepare. The frame's dt / input / size are already latched onto `frame`, so a callback sees this
            // frame's state, not the previous one's - which is what lets the world's queues be filled here rather
            // than in the record phase. frame.Commands is NOT recording during this call.
            frame.BeginSubmission(device);
            try
            {
                onPrepare?.Invoke(frame);

                // Record. Everything from here to the scope's close belongs in the frame's own list, and the scope is
                // what makes that enforceable rather than advisory: it claims the device in the seam's open-recording
                // register, so any engine API that opens a list of its own from inside onFrame refuses by name instead
                // of resetting this recording's device state (#424). A frame that renders nothing holds the default
                // not-recording scope, claims nothing, and leaves the prepare-phase freedom intact.
                GpuRecordingScope recording = render
                    ? GpuRecording.Open(device, commands, "the window's frame list")
                    : default;
                try
                {
                    if (render)
                    {
                        commands.SetFramebuffer(device.SwapchainFramebuffer!);
                        commands.ClearColorTarget(0, clearColor);
                    }
                    onFrame(frame);
                }
                finally { recording.Dispose(); }

                if (render)
                {
                    frame.Submission.Submit(commands);
                    device.Present();
                }
                else frame.Submission.Abandon();
            }
            catch
            {
                frame.Submission.Abandon();
                throw;
            }
        }
    }
}
