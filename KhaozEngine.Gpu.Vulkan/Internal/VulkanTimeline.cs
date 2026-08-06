using System;
using System.Diagnostics;
using System.Threading;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE DEVICE'S ONE COMPLETION TIMELINE, and everything the seam calls a fence sits on it: the value every
    /// submission signals, <see cref="IGpuFence"/>, the counted <c>WaitForIdle</c> drain, and the completion read
    /// the deferred-disposal retire list and (from row 8) the uniform ring's segment gate both poll. Decisions
    /// V-F1 to V-F4.
    ///
    /// <para><b>WHY ONE DEVICE TIMELINE RATHER THAN A <c>VkFence</c> PER SUBMIT, because this is the type where
    /// that is decided.</b> The seam promises that a fence handed to a submission made after some earlier work
    /// signals only once the queue has drained through it. With per-submit fences that is a CONVENTION, because
    /// fence B signalling says nothing at all about submission A. With one monotonic timeline it is a THEOREM: a
    /// timeline semaphore's signal operations must strictly increase, and a queue's signal operations on one
    /// semaphore execute in submission order, so the counter reaching 6 requires the signal at 5 to have happened,
    /// which requires submission 5's commands to have completed. Polling a later fence therefore transitively
    /// covers every earlier submission, which is exactly what <c>RetiredResourcePool</c> relies on and what the
    /// retire list below is built on.</para>
    ///
    /// <para><b>ONE <c>vkQueueSubmit</c> PER SUBMISSION (V-F3).</b> The incumbent's second empty submit signalling
    /// an internal tracking fence is not inherited. One timeline collapses three separate completion mechanisms
    /// (user fences, tracking fences, staging recycling) into one primitive, and a submit with no fence STILL
    /// takes a value, because the timeline has to advance with the submission stream for a later fence's value to
    /// cover the earlier work at all.</para>
    ///
    /// <para><b>EVERYTHING HERE IS DEVICE-FREE.</b> The native calls are three members on
    /// <see cref="IVulkanTimelineSemaphore"/> and the liveness is <see cref="IVulkanDeviceLiveness"/>, so the
    /// value allocation, the fence lifecycle, the dead-device answers and the drain accounting all run on a
    /// machine with no Vulkan loader. What is NOT exercised device-free is a value being signalled by real GPU
    /// work, because nothing submits yet: row 7 (https://github.com/APKiwiOrg/KhaozEngine/issues/517) owns the
    /// submit path and is where a value first advances because a GPU finished something.</para>
    ///
    /// <para><b>AFTER DEVICE DEATH EVERY ANSWER IS "DONE" (V-F10).</b> <see cref="CompletedValue"/> reports the
    /// last value ever allocated instead of touching a destroyed device's semaphore, so every fence reads
    /// signalled and every waiter is released. Answering anything else would strand a retire pool forever on a
    /// batch it can never free, which is a teardown-order hazard rather than a hypothetical.</para>
    /// </summary>
    internal sealed class VulkanTimeline : IDisposable
    {
        static readonly ILogger log = Log.For<VulkanTimeline>();

        readonly IVulkanTimelineSemaphore _semaphore;
        readonly IVulkanDeviceLiveness _liveness;

        // The last value ALLOCATED to a submission, which is not the same as the last value the GPU has reached.
        // Read after device death in place of asking the semaphore, since a destroyed device's objects must not be
        // touched and everything issued is complete by then anyway.
        ulong _issued;

        long _totalDrainCount;
        long _totalDrainTicks;

        bool _disposed;

        /// <param name="semaphore">The device's timeline semaphore. Disposed with this timeline.</param>
        /// <param name="liveness">The device's liveness token, or null while a caller does not have one, in which
        /// case the device is treated as alive forever. See <see cref="IVulkanDeviceLiveness"/> for why defaulting
        /// to alive is the safe direction.</param>
        internal VulkanTimeline(IVulkanTimelineSemaphore semaphore, IVulkanDeviceLiveness? liveness = null)
        {
            ArgumentNullException.ThrowIfNull(semaphore);

            _semaphore = semaphore;
            _liveness = liveness ?? VulkanLiveDevice.Instance;
        }

        /// <summary>The device's liveness, read through the hook. Fences read it before anything else.</summary>
        internal bool IsDeviceDead => _liveness.IsDead;

        /// <summary>
        /// The highest value ever allocated by <see cref="NextSubmitValue"/>, and therefore the value the GPU has
        /// to reach for every submission ever made to have completed. 0 before anything has been submitted.
        /// <para>This is what <c>WaitForIdle</c> waits for, which is the whole of V-F4: "the GPU is idle" and "the
        /// counter has reached the last value handed to a submit" are the same statement on a device with one
        /// queue that all work goes through.</para>
        /// </summary>
        internal ulong LastSubmitted => Volatile.Read(ref _issued);

        /// <summary>
        /// The SAME drains accumulated since the device was created, which is the half a telemetry session can
        /// carry. Nothing rolls it, so two sampled rows bracket a window exactly. See
        /// <see cref="VulkanWaitTotals"/>.
        /// </summary>
        internal VulkanWaitTotals TotalDrain => VulkanWaitTotals.Sample(ref _totalDrainCount, ref _totalDrainTicks);

        /// <summary>
        /// The counter's current value, as a NON-BLOCKING read, or the last value allocated once the device is
        /// dead.
        /// <para>
        /// LIVENESS IS CHECKED ON BOTH SIDES OF THE READ, and the second check is the one worth reading twice. A
        /// device loss can be discovered BY this very read, in which case the latch flips liveness underneath us
        /// and the number the driver handed back means nothing. Asking again afterwards is what stops that number
        /// reaching a fence, and it costs one volatile read on a path that just crossed the driver boundary.
        /// </para>
        /// </summary>
        internal ulong CompletedValue
        {
            get
            {
                if (_liveness.IsDead) return LastSubmitted;

                ulong read = _semaphore.Read();
                return _liveness.IsDead ? LastSubmitted : read;
            }
        }

        /// <summary>
        /// Take the next value for a submission. Strictly increasing, thread-safe, and the only place a value is
        /// ever handed out.
        /// <para>
        /// THREAD-SAFE BECAUSE THE SUBMIT PATH IS. Recording is lock-free on this backend and a submit from one
        /// thread must not be able to hand the same value to a submit from another, because two submissions
        /// sharing a value would make a fence on the first read signalled when only the second had finished.
        /// <see cref="Interlocked.Increment(ref ulong)"/> is the whole mechanism, and it is why this member is not
        /// merely <c>_issued + 1</c>.
        /// </para>
        /// <para>
        /// A VALUE IS SPENT WHETHER OR NOT ITS SUBMIT SUCCEEDS. Monotonicity is the property that matters and it
        /// is preserved: no value is handed out twice, and a submission that failed after taking one leaves a gap
        /// nothing ever reads. Row 7 owns the call site.
        /// </para>
        /// <para>
        /// PRECONDITION ON THE CALLER. The value must be allocated INSIDE whatever lock orders
        /// <c>vkQueueSubmit</c>, the submit that took the value must be the one that signals it, and every submit
        /// must take a value. Break this and two hazards follow. A submit that takes a value and fails with a
        /// non-loss result leaves <see cref="LastSubmitted"/> above anything the GPU will ever signal, so the next
        /// <c>WaitForIdle</c> blocks forever. Two threads allocating outside the submit lock can signal out of
        /// order, violating the timeline's strictly-increasing requirement that the type's own theorem rests on.
        /// Row 7 (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/517">#517</see>) owns the integration
        /// and must satisfy this precondition or replace the <see cref="LastSubmitted"/> target with a registered
        /// signal high-water.
        /// </para>
        /// </summary>
        internal ulong NextSubmitValue() => Interlocked.Increment(ref _issued);

        /// <summary>A fresh, unarmed fence on this timeline. The seam's <c>IGpuResourceFactory.CreateFence</c>
        /// lands here when row 9 (https://github.com/APKiwiOrg/KhaozEngine/issues/519) builds the factory, and
        /// there is no capability gate in front of it, because <c>SupportsCompletionFences</c> is unconditionally
        /// true on this backend.</summary>
        internal VulkanGpuFence CreateFence() => new(this);

        /// <summary>
        /// THE DRAIN (V-F4): <c>vkWaitSemaphores</c> on the last submitted value with an infinite timeout, counted
        /// into <c>DrainCount</c> and <c>DrainMs</c>.
        ///
        /// <para><b>NOT <c>vkQueueWaitIdle</c> and not <c>vkDeviceWaitIdle</c>, for two reasons.</b> It does not
        /// need the queue lock, so a drain from one thread does not block a submit from another until it finishes,
        /// and it gives a value to time. Teardown still calls <c>vkDeviceWaitIdle</c>, which is a different
        /// question asked at a moment when there is no submission left to protect.</para>
        ///
        /// <para><b>THERE IS NO C6-STYLE BET HERE.</b> The incumbent's Vulkan drain is already real. Phase 2's
        /// <c>WaitForIdleCore</c> was an empty method body and the whole win there was in making it exist. This is
        /// reproducing a working thing with a countable primitive, so nobody should look for that win twice, and
        /// there is deliberately no kill switch to restore a no-op that was never here.</para>
        ///
        /// <para><b>THREE CASES RETURN WITHOUT COUNTING, and each is a case where nothing blocked.</b> The device
        /// is dead (V-F10, a destroyed device has nothing to wait for). Nothing has ever been submitted, so there
        /// is no point on the timeline to wait for at all, which is the state this backend is in until row 7
        /// lands. And the counter has ALREADY passed the last submitted value, which is a caller who asked and
        /// found the GPU caught up. The seam's own <c>DrainCount</c> doc says a wait that found the GPU already
        /// caught up is not counted, and this is the backend where honouring that costs one non-blocking read.
        /// The other backend counts every drain past its early returns, because it signals a FRESH point per drain
        /// and therefore always has something outstanding to wait for. Here the target is the last SUBMITTED
        /// value, so a second drain with no submission between them has genuinely nothing to do.</para>
        ///
        /// <para><b>A WAIT THAT ENDED BECAUSE THE DEVICE DIED IS STILL COUNTED.</b> It blocked, for the time
        /// recorded, and dropping it would under-report exactly the drains a post-mortem cares about. What is not
        /// counted is a wait that never happened.</para>
        ///
        /// <para><b>THE COUNTERS ARE ACCUMULATED WITH <see cref="Interlocked"/></b>, unlike the other backend's,
        /// whose drain is serialised by its submit lock and refuses a re-entrant caller by name. This drain
        /// deliberately holds no lock (that is half of why it is a semaphore wait), so two threads can be inside
        /// it at once and a plain <c>++</c> would lose entries. It costs two interlocked adds on a path that just
        /// spent milliseconds blocked.</para>
        /// </summary>
        internal void WaitForIdle()
        {
            if (_liveness.IsDead) return;

            ulong target = LastSubmitted;
            if (target == 0) return;
            if (CompletedValue >= target) return;

            long start = Stopwatch.GetTimestamp();
            bool reached = _semaphore.WaitUntil(target);
            long elapsed = Stopwatch.GetTimestamp() - start;

            Interlocked.Increment(ref _totalDrainCount);
            Interlocked.Add(ref _totalDrainTicks, elapsed);

            // A false is a device loss, which the semaphore already latched with its own site name and its own
            // error line. This one says what it meant for the CALLER, which the latch cannot know: the drain
            // returned without the GPU having reached the point that was asked for. It can fire at most once per
            // device, because every later drain returns at the liveness check above.
            if (!reached)
            {
                log.Warn($"The native Vulkan backend's WaitForIdle stopped waiting for timeline value {target} "
                    + "because the device was LOST mid-drain. The work that was outstanding has not completed and "
                    + "never will. Every fence on this device now reads signalled and every later drain returns "
                    + "immediately, which is what releases anything waiting on it during teardown.");
            }
        }

        /// <summary>
        /// Destroy the semaphore, ONCE, and only while the device is still alive. Called from the device's
        /// teardown after its <c>vkDeviceWaitIdle</c> and before the liveness flip, which is the only window in
        /// which destroying a child object of the device is legal at all.
        /// <para>
        /// A DEAD DEVICE SKIPS THE NATIVE DESTROY, because <c>vkDestroyDevice</c> already destroyed every object
        /// made from it and calling into the loader afterwards aborts the process on the Vulkan path rather than
        /// failing quietly. Outstanding <see cref="VulkanGpuFence"/> instances hold no device object of their own,
        /// so they simply stop being able to observe anything new.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_liveness.IsDead) return;

            _semaphore.Dispose();
        }
    }
}
