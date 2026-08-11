using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>The proof a <see cref="RetiredResourcePool"/> frees a batch behind: something that can mark a point
    /// in the submission stream and later be asked, without blocking, whether the GPU has reached it.
    /// <para>An interface rather than the device directly so the pool's whole ripeness policy is headless-testable
    /// (a test drives the signal by hand and asserts exactly which frame a batch dies on), and so a device with no
    /// GPU-completion fence has one obvious way to say so: <see cref="Submit"/> returns null and the pool keeps its
    /// frame-count fallback.</para></summary>
    internal interface IRetireBarrier
    {
        /// <summary>Mark the current point in the submission stream. The returned fence signals once the GPU has
        /// finished EVERY command submitted before this call. Null when no fence could be issued, which puts the
        /// batch on the pool's frame-count fallback rather than failing.</summary>
        IGpuFence? Submit();

        /// <summary>Hand a signaled fence back once the batch it covered has been freed, so the next batch reuses
        /// it instead of allocating a new device fence every frame.</summary>
        void Release(IGpuFence fence);

        /// <summary>Free the barrier's own GPU objects (its command list and any pooled fences). Called at scene
        /// teardown, after everything the pool was holding has been destroyed.</summary>
        void Dispose();
    }

    /// <summary>The real <see cref="IRetireBarrier"/>: an empty command list submitted with a fence.
    ///
    /// <para><b>Why an empty submission proves anything.</b> A fence is signaled when the submission it was handed
    /// to completes. Submitting one with nothing in it, AFTER the frame whose work referenced the retired
    /// resources, therefore signals only once the queue has drained through all of that work. That is not an
    /// inference about driver behaviour: Vulkan specifies <c>vkQueueWaitIdle</c> as equivalent to submitting a
    /// fence to the queue and waiting on it with an infinite timeout, so a fence submitted here and later polled
    /// signaled carries EXACTLY the guarantee the <c>WaitForIdle</c> it replaces carried, taken at the moment of
    /// the submit instead of the moment of the free. Metal reaches the same place from the other side: a command
    /// queue executes its command buffers in commit order and Veldrid signals the fence from the buffer's
    /// completion handler, so an empty buffer committed last completes last.</para>
    ///
    /// <para>The cost is one command-buffer commit on frames that retired something, against the 1.5 to 1.6 ms
    /// pipeline stall it replaces.</para>
    ///
    /// <para><b>Not thread-safe</b>, and it does not need to be: it is driven from
    /// <see cref="RetiredResourcePool.BeginFrame"/> and nothing else, which is the frame thread by that type's own
    /// contract.</para></summary>
    internal sealed class GpuRetireBarrier : IRetireBarrier
    {
        readonly IGpuDevice _gd;
        readonly IGpuCommandList _cl;
        // Signaled fences handed back by the pool. Reset and reused, so a streaming session allocates a couple of
        // device fences total rather than one per drained frame.
        readonly Stack<IGpuFence> _free = new();

        /// <summary>Build a barrier on <paramref name="gd"/>, or null when the device cannot signal a fence on GPU
        /// completion (see <see cref="GpuCapabilities.SupportsCompletionFences"/>). Null is the whole fallback
        /// mechanism: the pool takes it and keeps draining on a frame count, which is the behaviour every backend
        /// had before fences existed here.</summary>
        public static IRetireBarrier? TryCreate(IGpuDevice gd)
            => gd.Capabilities.SupportsCompletionFences ? new GpuRetireBarrier(gd) : null;

        GpuRetireBarrier(IGpuDevice gd)
        {
            _gd = gd;
            // One command list for the whole scene lifetime, re-Begun per sealed batch. What makes that safe is
            // submission ORDER, not any claim about how a backend recycles command buffers: the fence handed to a
            // submission signals when that submission completes, and everything submitted before it has completed
            // by then (see the class note). Reusing the list cannot reorder that.
            // What it is NOT safe against is being Begun while ANOTHER list is recording. With Direct3D11 in
            // immediate-context mode a command list is the device's immediate context and Begin resets it, so this
            // Submit inside Scene3D.Begin would wipe an open frame's bindings (#423, and #424 for the site list).
            // That is no longer something a caller can reach silently: Submit opens through GpuRecording, so a
            // Scene3D.Begin called from inside a frame's recording throws GpuNestedRecordingException naming both
            // sides. The correct call site is unchanged and unaffected, since every host begins the scene in the
            // frame's pre-record phase.
            //
            // WHICH BACKENDS THAT IS TRUE OF, now that one of them issues real fences. On the Veldrid Direct3D11
            // leg it stays unreachable: that backend reports no completion fences, so TryCreate returns null and
            // no barrier exists. On the engine's own native Direct3D11 backend (GpuBackendKind.Direct3D11Native)
            // fences ARE real, so a barrier is built there, and its default recording driver is what makes that
            // safe: recording appends to an engine-owned command stream and touches no device state at all, so a
            // Begin here cannot disturb an open recording, and the frame's own replay opens with its own
            // ClearState and a reset redundancy cache, so nothing carries over from this empty submission. Under
            // KE_D3D11_RECORD=immediate the hazard is exactly as described above and the barrier is exactly as
            // unsafe as it is on the incumbent, which is one of the reasons that driver is an A/B lever rather
            // than a supported configuration (decision M1, spec section 10.3).
            _cl = gd.Factory.CreateCommandList();
        }

        public IGpuFence? Submit()
        {
            IGpuFence fence;
            if (_free.Count > 0) { fence = _free.Pop(); fence.Reset(); }
            else fence = _gd.Factory.CreateFence();

            // The body is deliberately empty: the submission itself is the marker, not anything recorded into it
            // (see the class note). Opened through the seam's register so a barrier fired from inside an open
            // recording refuses by name rather than resetting that recording's device state (#424).
            using (GpuRecording.Open(_gd, _cl, "GpuRetireBarrier.Submit")) { }
            _gd.Submit(_cl, fence);
            return fence;
        }

        public void Release(IGpuFence fence) => _free.Push(fence);

        public void Dispose()
        {
            while (_free.Count > 0) _free.Pop().Dispose();
            _cl.Dispose();
        }
    }
}
