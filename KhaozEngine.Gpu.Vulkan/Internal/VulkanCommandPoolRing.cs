using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// ONE COMMAND LIST'S POOLS: <see cref="VulkanFramesInFlight"/> <c>VkCommandPool</c>s, one primary
    /// <c>VkCommandBuffer</c> allocated out of each at construction, and a parallel array of the timeline value
    /// each slot was last submitted at. Decisions V-R2 and V-R3, section 6.1.
    ///
    /// <para><b>A POOL PER SLOT, NOT ONE POOL WITH <c>RESET_COMMAND_BUFFER</c>.</b> The incumbent creates one pool
    /// per list with that flag, which tells the driver every buffer must be individually resettable and pushes it
    /// onto the slower per-buffer allocator. Resetting the WHOLE pool is the documented fast path and returns the
    /// last record's memory to the pool's arena in one operation. The cost is three pool objects per list instead
    /// of one. The flag is not merely unused: <see cref="IVulkanCommandApi.CreatePool"/> has no parameter through
    /// which it could be asked for.</para>
    ///
    /// <para><b>THE WRAP IS THE BACKPRESSURE.</b> <see cref="Advance"/> moves to the next slot and waits on the
    /// value that slot was last submitted at, which in the steady state at depth 3 is a value the GPU passed two
    /// records ago and returns immediately from one non-blocking read. When it does NOT, the CPU is more than
    /// <see cref="Depth"/> records ahead of the GPU on this list, and that wait is recorded into
    /// <see cref="VulkanBackpressure"/>, which is MV3's exit criterion. A poll that finds the value already
    /// reached records NOTHING, because a counter that ticks on a non-wait cannot answer "was anything ever
    /// blocked" with a zero.</para>
    ///
    /// <para><b>THE DEPTH IS SHARED WITH THE UNIFORM RING AND THE INDEX IS NOT.</b> This slot advances on every
    /// <c>Begin</c> and belongs to one list. The ring's segment advances at the FRAME boundary and belongs to the
    /// device. A list begun twice in one frame takes two slots here and writes one segment there, which is
    /// correct in both directions: two records must not share a command buffer still in flight, and two records in
    /// one frame must see one frame's uniform values. See <see cref="VulkanFramesInFlight"/> for the one number
    /// behind both.</para>
    ///
    /// <para><b>NOTHING HERE IS THREAD-SAFE, AND THAT IS THE POINT.</b> A <c>VkCommandPool</c> and every buffer
    /// allocated from it are EXTERNALLY SYNCHRONISED, one thread at a time. Per-list pools mean two lists
    /// recording on two threads never touch the same pool, so N lists record concurrently and genuinely with no
    /// lock anywhere on the record path (V-R4). What that requires from a caller is exactly what the seam already
    /// requires: one thread at a time per LIST. A single list driven from two threads is a data race here, and it
    /// would be one on the driver's side too.</para>
    ///
    /// <para><b>DISPOSAL HANDS THE POOLS TO THE RETIRE LIST</b> (V-F9) at the highest value any of its slots was
    /// submitted at, so a list disposed with submissions outstanding destroys nothing until the GPU has passed
    /// them. The incumbent uses a refcount, which also works and which this design does not need because the
    /// retire list exists for resources anyway. Every held destroy is TERMINAL (it allocates nothing and retires
    /// nothing), which is what makes it legal in the teardown drain that runs between <c>vkDeviceWaitIdle</c> and
    /// <c>vkDestroyDevice</c>.</para>
    /// </summary>
    internal sealed class VulkanCommandPoolRing
    {
        readonly IVulkanCommandApi _api;
        readonly VulkanTimeline _timeline;
        readonly VulkanBackpressure _backpressure;

        readonly ulong[] _pools;
        readonly ulong[] _buffers;

        // THE VISIBILITY CONTRACT: written by RecordSubmitted under the submit lock, and read by WaitForSlot
        // (inside Advance) with no lock at all. That is correct when the thread calling Advance is the same
        // thread that called Submit, or when the consumer's own handoff between a recording thread and a
        // submitting thread supplies the memory barrier a lock would otherwise give this read. Nothing in this
        // type enforces either side of that. It is the seam's documented threading model, not this array's own.
        readonly ulong[] _lastSubmitted;

        // -1 so the first Advance lands on slot 0 rather than on slot 1. A ring that started at 0 and
        // pre-incremented would leave slot 0 unused until the first wrap, which is a whole pool per list allocated
        // and never touched.
        int _slot = -1;

        /// <param name="api">The native command seam.</param>
        /// <param name="framesInFlight">The depth, from <see cref="VulkanFramesInFlight"/>. Resolved once per
        /// device, not once per list, so every list on a device has the same depth.</param>
        /// <param name="timeline">The device's one completion timeline, which slot waits are taken against.</param>
        /// <param name="backpressure">The device's one backpressure accumulator (MV3).</param>
        internal VulkanCommandPoolRing(IVulkanCommandApi api, int framesInFlight, VulkanTimeline timeline,
            VulkanBackpressure backpressure)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(timeline);
            ArgumentNullException.ThrowIfNull(backpressure);

            if (framesInFlight < VulkanFramesInFlight.Minimum || framesInFlight > VulkanFramesInFlight.Maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(framesInFlight), framesInFlight,
                    $"A native Vulkan command list runs between {VulkanFramesInFlight.Minimum} and "
                    + $"{VulkanFramesInFlight.Maximum} command pools. {VulkanFramesInFlight.EnvVarName} clamps to "
                    + "that range before it gets here.");
            }

            _api = api;
            _timeline = timeline;
            _backpressure = backpressure;

            _pools = new ulong[framesInFlight];
            _buffers = new ulong[framesInFlight];
            _lastSubmitted = new ulong[framesInFlight];

            // EVERY POOL AND EVERY BUFFER UP FRONT, so no record path ever creates a driver object: a Begin that
            // could allocate is a Begin that could fail on frame 4000, and it is the shape #423's DEVICE_REMOVED
            // stacks all came out of on the other backend. A failure part way through leaves this constructor
            // owning pools nothing else knows about, so it destroys them before it lets the throw out.
            int created = 0;
            try
            {
                for (; created < framesInFlight; created++)
                {
                    _pools[created] = _api.CreatePool();
                    _buffers[created] = _api.AllocatePrimaryBuffer(_pools[created]);
                }
            }
            catch
            {
                for (int i = 0; i < created; i++) _api.DestroyPool(_pools[i]);
                if (created < framesInFlight && _pools[created] != 0) _api.DestroyPool(_pools[created]);
                throw;
            }
        }

        /// <summary>How many slots this ring has, which is the device's <see cref="VulkanFramesInFlight"/>.
        /// </summary>
        internal int Depth => _pools.Length;

        /// <summary>The slot the last <see cref="Advance"/> landed on, or -1 before the first one.</summary>
        internal int Slot => _slot;

        /// <summary>The highest timeline value any slot of this ring was submitted at, which is the value its
        /// pools are retired behind. 0 when nothing this list recorded was ever submitted, and an entry retired at
        /// 0 is released by the very next drain, which is correct: pools no submission ever referenced are safe to
        /// destroy immediately.</summary>
        internal ulong HighestSubmitted
        {
            get
            {
                ulong highest = 0;
                for (int i = 0; i < _lastSubmitted.Length; i++)
                {
                    if (_lastSubmitted[i] > highest) highest = _lastSubmitted[i];
                }

                return highest;
            }
        }

        /// <summary>
        /// Advance to the next slot, WAIT for that slot's last submission to complete, reset its whole pool, and
        /// begin its buffer with <c>ONE_TIME_SUBMIT</c>. The three native calls a <c>Begin</c> is, in the one
        /// order that makes them safe: nothing is reset until the GPU has finished reading it.
        /// </summary>
        /// <returns>The slot now being recorded into.</returns>
        internal int Advance()
        {
            int slot = _slot + 1;
            if (slot >= _pools.Length) slot = 0;

            WaitForSlot(slot);

            _api.ResetPool(_pools[slot]);
            _api.BeginOneTimeSubmit(_buffers[slot]);

            // AFTER the native calls, so a throw out of the reset or the begin leaves the ring where it was
            // rather than on a slot whose pool was never reset. A caller that retries then retries the same slot.
            _slot = slot;
            return slot;
        }

        /// <summary><c>vkEndCommandBuffer</c> on <paramref name="slot"/>'s buffer, sealing it for
        /// submission.</summary>
        internal void EndRecording(int slot) => _api.EndRecording(_buffers[slot]);

        /// <summary>The buffer belonging to <paramref name="slot"/>, which the submit path names in its
        /// <c>VkSubmitInfo</c>.</summary>
        internal ulong BufferAt(int slot) => _buffers[slot];

        /// <summary>The value <paramref name="slot"/> was last submitted at, or 0 if it never was. The number the
        /// next wrap onto that slot waits for.</summary>
        internal ulong SubmittedAt(int slot) => _lastSubmitted[slot];

        /// <summary>
        /// Record that <paramref name="slot"/>'s buffer went to the queue at timeline value
        /// <paramref name="value"/>. Written by the submit path inside its own lock, and read by the next
        /// <see cref="Advance"/> that wraps onto this slot.
        /// </summary>
        internal void RecordSubmitted(int slot, ulong value) => _lastSubmitted[slot] = value;

        /// <summary>
        /// Hand every pool to <paramref name="retired"/>, held behind <see cref="HighestSubmitted"/>. The list's
        /// disposal, and the only place these pools are ever destroyed.
        /// <para>
        /// ONE ENTRY PER POOL rather than one entry closing over the array, because the retire list invokes its
        /// callbacks outside its own lock and a callback that threw part way through a loop would leak the pools
        /// after it. Each entry is one <c>vkDestroyCommandPool</c>, which frees the buffer allocated from it too.
        /// </para>
        /// </summary>
        /// <param name="retired">The device's deferred-disposal list.</param>
        internal void RetireInto(VulkanRetireList retired)
        {
            ArgumentNullException.ThrowIfNull(retired);

            ulong value = HighestSubmitted;
            for (int i = 0; i < _pools.Length; i++)
            {
                ulong pool = _pools[i];
                retired.Retire(value, () => _api.DestroyPool(pool));
            }
        }

        /// <summary>The pool handles, in slot order. Diagnostic and test surface: the ring's own callers name
        /// slots rather than pools.</summary>
        internal IReadOnlyList<ulong> Pools => _pools;

        // The wait itself, and the whole of the "counted as backpressure when it does not return immediately"
        // rule. The poll first, then the block, and only the block is recorded.
        void WaitForSlot(int slot)
        {
            ulong recorded = _lastSubmitted[slot];

            // Nothing this slot recorded has ever been submitted, so there is no work to wait for. The first
            // Depth records of a list's life all take this path, which is why a freshly created list starts
            // recording without touching the timeline at all.
            if (recorded == 0) return;

            // A dead device answers its own last allocated value here, which is at or above anything a slot can
            // hold, so this returns without waiting rather than blocking on a counter nothing can advance.
            if (_timeline.CompletedValue >= recorded) return;

            // A device loss landing between the poll above and the wait below still records one near-zero entry
            // here, because WaitForValue returns fast once the device is dead. That is the honest direction: a
            // wait that ended because the device died is still counted rather than dropped. But it means a loss
            // inside a capture window can put a 1 into a counter whose exit criterion is zero, so a gate-4
            // reading with a device loss in the window is judged on the loss, not on the count.
            long start = Stopwatch.GetTimestamp();
            _timeline.WaitForValue(recorded);
            _backpressure.Record(Stopwatch.GetTimestamp() - start);
        }
    }
}
