using System;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// The order of one windowed frame: PREPARE, with no command list open, then RECORD, inside the frame's list.
    /// <see cref="AppWindow.Run(Action{Frame}, Action{Frame})"/> does nothing else per frame, so this type IS the
    /// loop's phase contract rather than a description of it.
    /// <para>
    /// <b>Why the split has to exist in the loop and not only in the renderer.</b> Some per-frame GPU work cannot be
    /// recorded into the frame's list at all: a compute dispatch whose output another dispatch reads in the same
    /// frame has no dispatch-to-dispatch barrier at the GPU seam
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/311">#311</see>), so its only ordering is a submit
    /// plus a device wait, which means a command list of its own. With Direct3D11 in immediate-context mode a command
    /// list IS the device's immediate context and opening one calls <c>ClearState</c> on it, so opening a second list
    /// while the frame's is recording wipes every binding the frame believes is live and the device faults a few
    /// draws later (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/423">#423</see>). The headless hosts
    /// could honour that on their own because they open the frame's list themselves. The windowed loop opened it
    /// before calling back, so no host running on a window could
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/429">#429</see>).
    /// </para>
    /// <para>
    /// It knows nothing about 3D, water, or what a producer is. It only guarantees WHEN the two callbacks run
    /// relative to the frame's command list, which is the one fact a producer cannot establish for itself.
    /// </para>
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
                device.Submit(commands);
                device.Present();
            }
        }
    }
}
