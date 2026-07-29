using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>Deferred disposal for GPU resources freed mid-life (a streamed mesh unloaded while the scene keeps
    /// running). Retirements are grouped into a per-frame BATCH at the next frame boundary, each batch is stamped
    /// with a GPU fence submitted at that boundary, and a batch is destroyed on the first later frame boundary
    /// whose fence polls signaled. Nothing blocks: a burst of unloads costs one empty fenced submission, not a
    /// pipeline stall.
    /// <para>The rule this preserves: a GPU resource is never destroyed while queued work may still reference it
    /// (Mesa lavapipe runs submissions on its own thread and segfaults on the use-after-free, which is why the
    /// disposal sites drained at all, see 8c2a6c6b). Nothing here weakens that, and the fence path is strictly
    /// stronger than the frame count it replaces:</para>
    /// <list type="number">
    /// <item>A batch sealed as the counter moves to F holds exactly the resources retired while the counter read
    /// F-1 or less. Every command that could reference one of them was recorded into a frame whose command list
    /// was already submitted by then, because a host submits each frame's work between consecutive
    /// <see cref="BeginFrame"/> calls (that is the contract, see the host note below).</item>
    /// <item>The batch's fence is submitted at that same boundary, so it sits AFTER all of that work in the
    /// submission stream. Polling it signaled therefore carries the same guarantee the <c>WaitForIdle</c> it
    /// replaced carried, taken at seal time rather than at free time. See <see cref="GpuRetireBarrier"/> for the
    /// spec citation.</item>
    /// <item>Batches are freed strictly oldest-first and the sweep STOPS at the first unsignaled fence, so a batch
    /// is only ever destroyed after every older batch already was.</item>
    /// </list>
    /// <para><b>The fallback.</b> On a device with no GPU-completion fence
    /// (<see cref="GpuCapabilities.SupportsCompletionFences"/> false: Direct3D11 and OpenGL, where Veldrid's fence
    /// is a CPU-side submit receipt) the barrier is absent, and a batch instead waits out
    /// <see cref="FrameDelay"/> frame boundaries and is destroyed behind one <c>WaitForIdle</c>. That is exactly
    /// the pre-fence behaviour, unchanged, so an unfenced backend loses the speed-up and keeps every safety
    /// property it had.</para>
    /// <para>The renderers that grow-and-retire a buffer (<c>ModelRenderer</c>, <c>ParticleRenderer</c>,
    /// <c>GroundDecalRenderer</c>, <c>OverlayMeshRenderer</c>, <c>ShadowMapRenderer</c>) keep their retired list until
    /// teardown, which is correct for a handful of geometric grows and wrong for a streaming path that retires
    /// megabytes a minute. This type is the streaming form of the same rule.</para>
    /// <para><b>Not thread-safe.</b> It is the scene's frame loop and nothing else: every member touches the same
    /// unsynchronized lists and frame counter, so retiring from a background build thread while the frame thread is
    /// in <see cref="BeginFrame"/> corrupts it. A worker that wants a resource freed hands it to the frame thread
    /// first, the way the streamer's apply step does.</para>
    /// <para><b>It only frees on <see cref="BeginFrame"/>.</b> Nothing here is time-driven, so a scene that retires
    /// but never calls Begin holds every retired resource until <see cref="FlushAll"/> or teardown, which for a
    /// streaming world is the whole unloaded ring. A host that drives the scene without a frame boundary (a tool, a
    /// test, an offscreen render) must call one of the two itself.</para>
    /// <para><b>Host contract.</b> A frame's command list must be submitted before the NEXT
    /// <see cref="BeginFrame"/>. Every host does this (<c>AppWindow.Run</c> submits and presents at the end of the
    /// frame, <c>Render3DPreview.Capture</c> and <c>Render3DSnapshot.Capture</c> submit inside the call that
    /// rendered), and point 1 above rests on it. A host that recorded draws across two Begin calls and submitted
    /// once at the end would break it, and would have been equally broken by the frame-count scheme.</para></summary>
    internal sealed class RetiredResourcePool : IDisposable
    {
        /// <summary>Frame boundaries a retired resource waits before it is destroyed ON THE FALLBACK PATH (no
        /// GPU-completion fence). Three covers the deepest CPU-ahead-of-GPU window a vsynced frame loop reaches, so
        /// by the time the drain runs the referencing work has long completed. The fence path does not use it: a
        /// signaled fence is proof, where a frame count is only a bet that three frames is enough.</summary>
        public const int DefaultFrameDelay = 3;

        readonly Action _drainDevice;
        readonly IRetireBarrier? _barrier;
        // Retired resources, appended in retirement order and freed from the front. The retirement FRAME is not
        // stored per entry: batching at the frame boundary means one batch is exactly one frame's retirements, so
        // the batch carries the single stamp the fallback ripeness test needs.
        readonly List<IDisposable> _pending = new();
        // Sealed batches, oldest first. Each owns a contiguous run at the FRONT of _pending, so freeing one is a
        // RemoveRange from index 0 and the "entries are in non-decreasing frame order" invariant does the rest.
        readonly List<Batch> _batches = new();
        // Entries covered by _batches. The tail beyond it is this frame's retirements, not yet sealed.
        int _batched;

        // Frame boundaries seen. Wraps at int.MaxValue after about a year of continuous 60 Hz play. The age
        // subtraction below stays correct across the wrap because it is unchecked two's complement.
        int _frame;

        /// <summary>Build a pool over the device drain it runs before destroying anything on the fallback path
        /// (normally <c>IGpuDevice.WaitForIdle</c>) and, when the device can signal a fence on GPU completion, the
        /// <paramref name="barrier"/> that replaces that drain with a poll. A null barrier is the supported
        /// fallback, not an error. <paramref name="frameDelay"/> below 1 is clamped to 1, so a resource is never
        /// destroyed inside the call that retired it.</summary>
        public RetiredResourcePool(Action drainDevice, IRetireBarrier? barrier = null, int frameDelay = DefaultFrameDelay)
        {
            _drainDevice = drainDevice ?? throw new ArgumentNullException(nameof(drainDevice));
            _barrier = barrier;
            FrameDelay = frameDelay < 1 ? 1 : frameDelay;
        }

        /// <summary>Frame boundaries a retired resource waits on the FALLBACK path (at least 1).</summary>
        public int FrameDelay { get; }

        /// <summary>Resources retired but not yet destroyed. Counts the whole holding, sealed batches and this
        /// frame's not-yet-sealed tail alike, which is what <c>Scene3D.RetiredResourceCount</c> surfaces.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>Batches sealed and waiting on their fence. Diagnostic seam for the tests that pin how many
        /// fenced submissions a churn pattern actually costs.</summary>
        internal int SealedBatchCount => _batches.Count;

        /// <summary>Hand a resource over to the pool. Costs nothing at the call site: no drain, no destroy, no
        /// submission. A null resource is ignored, so an optional resource (a per-mesh material set) needs no
        /// caller-side check.</summary>
        public void Retire(IDisposable? resource)
        {
            if (resource is null) return;
            _pending.Add(resource);
        }

        /// <summary>Retire up to three related resources in one call (a mesh's vertex buffer, index buffer and
        /// optional material set), so the caller needs no null checks and no per-resource statement.</summary>
        public void Retire(IDisposable? a, IDisposable? b, IDisposable? c)
        {
            Retire(a); Retire(b); Retire(c);
        }

        /// <summary>Advance one frame: destroy every batch the GPU has provably finished with, then seal whatever
        /// was retired during the frame just ended behind a fresh fence. A frame with nothing pending and nothing
        /// newly retired touches the device not at all.</summary>
        public void BeginFrame()
        {
            _frame++;
            // SEAL FIRST, then sweep. Two reasons, and the second is the one that is easy to get wrong.
            // (a) It keeps the fallback path bit-for-bit identical to the pre-fence per-entry scheme at every
            //     FrameDelay, including the degenerate FrameDelay of 1 where a batch was ripe on the very boundary
            //     that closed it. Sweeping first would have quietly added a frame there.
            // (b) It is safe on the fence path even though it polls a fence submitted moments earlier in the same
            //     call. A fence that already reads signaled means the GPU really has finished everything submitted
            //     before it, so freeing right then is correct rather than lucky. The sweep still cannot reach the
            //     new batch before every older one has gone, because it walks the list from the front and stops.
            SealNewBatch();
            FreeRipeBatches();
        }

        // Free the ripe PREFIX of _batches, stopping at the first batch that is not ripe. Stopping (rather than
        // skipping ahead to a later batch whose fence happens to be signaled) is load-bearing: it is what makes
        // "this batch died" imply "every older batch died first".
        void FreeRipeBatches()
        {
            bool drained = false;
            while (_batches.Count > 0)
            {
                Batch b = _batches[0];
                if (b.Fence is { } fence)
                {
                    if (!fence.Signaled) break;                        // the GPU has not reached the seal point yet
                }
                else
                {
                    if (_frame - b.MaxRetiredAt < FrameDelay) break;   // fallback: the pre-fence frame count
                    if (!drained) { _drainDevice(); drained = true; }  // one drain covers every batch freed here
                }

                for (int i = 0; i < b.Count; i++) _pending[i].Dispose();
                _pending.RemoveRange(0, b.Count);
                _batched -= b.Count;
                _batches.RemoveAt(0);
                if (b.Fence is { } spent) _barrier!.Release(spent);
            }
        }

        // Seal what was retired since the last boundary into its own batch and mark the submission stream with a
        // fence. Appends, so the new batch is always behind every older one.
        void SealNewBatch()
        {
            int fresh = _pending.Count - _batched;
            if (fresh == 0) return;
            // The newest resource in this batch was retired while the counter read _frame - 1, so the fallback
            // ripeness test above stays the same "_frame - RetiredAt >= FrameDelay" the per-entry scheme used.
            _batches.Add(new Batch(fresh, _barrier?.Submit(), _frame - 1));
            _batched = _pending.Count;
        }

        /// <summary>Destroy everything pending right now, draining once first. For teardown, where waiting out a
        /// fence (or the frame delay) would leak the tail. This path keeps the drain on purpose even where fences
        /// are available: shutdown is the one place correctness is worth more than the stall, and a poll would have
        /// to spin.</summary>
        public void FlushAll()
        {
            // A batch always owns at least one entry, so an empty holding means there are no batches either and
            // there is nothing to drain for. Drain BEFORE anything else: it is what makes both the disposals below
            // and the fence recycling safe (a recycled fence gets Reset on its next submit, and resetting one still
            // in flight is a validation error, so the drain has to have retired every in-flight submission first).
            if (_pending.Count == 0) return;
            _drainDevice();
            for (int i = 0; i < _pending.Count; i++) _pending[i].Dispose();
            _pending.Clear();
            foreach (Batch b in _batches) if (b.Fence is { } fence) _barrier!.Release(fence);
            _batches.Clear();
            _batched = 0;
        }

        /// <summary>Flush everything pending, then free the barrier's own GPU objects. Scene teardown.</summary>
        public void Dispose()
        {
            FlushAll();
            _barrier?.Dispose();
        }

        // One frame's worth of retirements, sealed at a frame boundary. A null fence means this batch is on the
        // frame-count fallback (no barrier at all, or a barrier that could not issue one).
        readonly struct Batch
        {
            public readonly int Count;
            public readonly IGpuFence? Fence;
            public readonly int MaxRetiredAt;
            public Batch(int count, IGpuFence? fence, int maxRetiredAt)
            {
                Count = count; Fence = fence; MaxRetiredAt = maxRetiredAt;
            }
        }
    }
}
