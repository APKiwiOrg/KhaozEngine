using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// M-G5's NATIVE FRAME CAPTURE. This device owns its <c>MTLCommandQueue</c>, so an armed
    /// <see cref="GpuFrameCapture"/> is serviced with the pointer in hand and the reflection into Veldrid's
    /// private <c>_commandQueue</c> field never runs on this path.
    /// <para>
    /// <b>THIS IS THE THIRD OF THE THREE SITES THE METAL APPEND DEGRADED SILENTLY (4.2), AND IT IS THE ONE THAT
    /// COULD NOT BE FIXED BY WIDENING A GATE.</b> <c>GpuFrameCapture.VeldridPathCaptures</c> answers true for
    /// <c>GpuBackendKind.Metal</c> alone, and widening it to the Metal FAMILY would have fixed nothing: that
    /// check lives inside the Veldrid device wrapper, which a provider-built native device never becomes. So the
    /// native backend services its own captures instead, which is both the correct fix and the one that removes
    /// the reflection.
    /// </para>
    /// <para>
    /// <b>THE CONSUMPTION SITE IS THE PRESENT BOUNDARY, WHICH IS THE SWAPCHAIN ROW'S</b>
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/581). A capture has to span a whole frame rather than one
    /// submit, because a frame's GPU work is several submits (the offscreen model pass, then the composite) and
    /// wrapping a single one catches the wrong command buffer. That makes "between two presents" the only correct
    /// bracket, so row 15 calls <see cref="ServiceFrameCaptureAtPresentBoundary"/> after it presents the drawable
    /// and this row builds the thing it calls. Until then an armed capture on a HEADLESS native device is never
    /// consumed, which is exactly the honest position: a headless device presents no frames, so there is no frame
    /// to capture.
    /// </para>
    /// </summary>
    internal sealed partial class MetalGpuDevice
    {
        bool _capturing;
        string _capturePath = "";

        /// <summary>
        /// Service an armed one-shot frame capture, at a present boundary. Call AFTER the drawable has been
        /// presented, so the capture brackets whole frames: the present that consumes the arm starts the trace,
        /// and the next present ends it.
        /// <para>
        /// A NO-OP ON EVERY ORDINARY FRAME. Nothing is armed, so this reads one flag and a lock-guarded null and
        /// returns, which is what makes it safe to call unconditionally from the frame loop. Nothing is captured
        /// either without <c>MTL_CAPTURE_ENABLED=1</c> in the environment BEFORE the process launched, and
        /// <see cref="MetalFrameCapture"/> asks Metal whether the destination is supported before it starts, so
        /// the ordinary case never reaches a call that could raise.
        /// </para>
        /// <para>
        /// THERE IS NO BACKEND-KIND GATE HERE, unlike the Veldrid wrapper's. This code runs only inside a native
        /// Metal device, so the question "is this the backend that services captures" has one answer by
        /// construction, and a second check of it would be a way for the two answers to disagree.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void ServiceFrameCaptureAtPresentBoundary()
        {
            bool consumed = false;
            if (!_capturing && GpuFrameCapture.TryConsume(out string path))
            {
                consumed = true;
                _capturePath = path;
            }

            switch (GpuFrameCapture.NextAction(_capturing, consumed))
            {
                case GpuFrameCapture.CaptureAction.StartAfterPresent:
                    // THE POINTER, NOT A REFLECTION. This is the whole of M-G5 in one argument.
                    _capturing = MetalFrameCapture.Start(Queue.Handle, _capturePath);
                    break;
                case GpuFrameCapture.CaptureAction.StopAfterPresent:
                    // The device's own drain travels in, so the trace closes with the captured frame's work
                    // finished. On this backend that is M-F5's counted drain, which is the correct one to spend
                    // here: a capture is a debug session and its drain belongs in the same channel as any other.
                    MetalFrameCapture.Stop(WaitForIdle);
                    _capturing = false;
                    break;
            }
        }
    }
}
