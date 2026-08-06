using System;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE SUBMIT PATH, and the one serialised point in the frame. One <c>vkQueueSubmit</c> per submission (V-F3),
    /// under one short lock, with the timeline value allocated INSIDE that lock.
    ///
    /// <para><b>WHY THE ALLOCATION IS INSIDE THE LOCK, which is the precondition
    /// <see cref="VulkanTimeline.NextSubmitValue"/> states and this type satisfies.</b> A timeline semaphore's
    /// signal operations must STRICTLY INCREASE, and that is the property the whole one-timeline theorem rests on
    /// (section 10.1). Allocating outside the lock lets two threads take values 5 and 6 and then reach
    /// <c>vkQueueSubmit</c> in the other order, so the queue is asked to signal 6 and then 5, which is a spec
    /// violation that a validation layer reports and a driver may implement as anything at all. Allocate first
    /// inside the lock and submit second inside the same lock, and allocation order IS submission order by
    /// construction, with no window between them for another thread to fit into.</para>
    ///
    /// <para><b>EVERY SUBMIT TAKES A VALUE, including one with no fence.</b> The timeline has to advance with the
    /// submission stream for a later fence's value to cover the earlier work at all, which is the transitivity the
    /// retire list and <c>RetiredResourcePool</c> both rely on.</para>
    ///
    /// <para><b>A FAILED SUBMIT CANNOT STRAND <c>WaitForIdle</c>, AND IT IS PREVENTED STRUCTURALLY RATHER THAN
    /// REPAIRED.</b> The hazard is real and specific: the two out-of-memory results do NOT flip liveness, and the
    /// spec requires the implementation to leave every referenced synchronisation primitive unaffected on those
    /// results, so a value taken by a submission that failed will never be signalled by anything. If the drain
    /// targeted the ALLOCATION high-water it would then wait forever on the next <c>WaitForIdle</c>. So this type
    /// raises <see cref="VulkanTimeline.LastSubmitted"/> only after <c>vkQueueSubmit</c> has returned success, and
    /// a failed submission simply leaves a hole in the value space that nothing ever waits on.</para>
    ///
    /// <para><b>THE HOST-SIGNAL REPAIR WAS WEIGHED AND DECLINED, and the reason is not tidiness.</b> The
    /// alternative is to keep the drain targeting the allocation high-water and close the gap by host-signalling
    /// the taken value with <c>vkSignalSemaphore</c>, which the binding spike proves exists. It is wrong here for
    /// a reason that only shows up under load: a host signal must also respect the strictly-increasing rule
    /// against the signals still PENDING on the queue. Take value 8, fail, and host-signal 8 while submissions 6
    /// and 7 are still executing, and the counter reaches 8 before the queue signals 6, which is a decreasing
    /// signal and undefined behaviour. Doing it correctly means first BLOCKING until the counter reaches 7, inside
    /// the submit lock, on the worst path in the backend (the machine is out of memory) and depending on a spec
    /// corner that no device-free test can exercise. The high-water costs one comparison, needs no native call at
    /// all, is provable device-free, and makes the drain target reachable BY CONSTRUCTION rather than by a repair
    /// that has to run correctly at the worst possible moment. <see cref="IVulkanTimelineSemaphore"/>'s "there is
    /// no signal member here, deliberately" therefore stands unweakened, which is the second thing the decline
    /// buys.</para>
    ///
    /// <para><b>THE FAILURE IS STILL THROWN.</b> A submission that did not happen is not something a caller may
    /// find out about from a counter. What the caller does NOT get is a hung drain on the way to finding out.
    /// </para>
    ///
    /// <para><b>EVERYTHING HERE IS DEVICE-FREE.</b> The native call is one member on
    /// <see cref="IVulkanCommandApi"/>, so the lock ordering, the allocation point, the registration rule, the
    /// failure shape, the fence arming and the slot write-back all run under <c>dotnet test</c> on a machine with
    /// no Vulkan loader.</para>
    /// </summary>
    internal sealed class VulkanSubmitQueue
    {
        static readonly ILogger log = Log.For<VulkanSubmitQueue>();

        // THE ONE SERIALISED POINT IN THE FRAME (V-W8). Recording is lock-free and takes nothing, and this lock is
        // held across exactly two operations: taking the next timeline value and handing one command buffer to the
        // queue.
        //
        // IT IS THE DEVICE'S LOCK RATHER THAN THIS TYPE'S, because the uniform ring's off-timeline write takes the
        // same one (9.2): a device-level UpdateBuffer must not land in the middle of a submit, and the ring's
        // segment-owner read is only exact while no submission sits between allocating its value and registering
        // it, which is a window that exists inside this lock alone. A ring with a second lock would order nothing.
        readonly object _submitLock;

        readonly IVulkanCommandApi _api;
        readonly VulkanTimeline _timeline;
        readonly ILogger _log;

        /// <param name="api">The native command seam.</param>
        /// <param name="timeline">The device's one completion timeline.</param>
        /// <param name="submitLock">The device's single submit lock, or null to own one. Null is for the tests that
        /// drive this type alone: a real device always passes its own, because the uniform ring gates on the same
        /// lock.</param>
        /// <param name="logger">The sink, or null for this type's own category logger.</param>
        internal VulkanSubmitQueue(IVulkanCommandApi api, VulkanTimeline timeline, object? submitLock = null,
            ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(timeline);

            _api = api;
            _timeline = timeline;
            _submitLock = submitLock ?? new object();
            _log = logger ?? log;
        }

        /// <summary>
        /// Submit <paramref name="list"/>'s sealed recording, optionally arming <paramref name="fence"/> with the
        /// value it signals.
        /// </summary>
        /// <param name="list">A list whose <c>End</c> has sealed a record.</param>
        /// <param name="fence">The fence to arm, or null for a submit with no fence. It must be unarmed, and it is
        /// armed BEFORE the native call so that a caller re-submitting an armed fence is refused without work
        /// having been queued.</param>
        /// <returns>The timeline value this submission signals, or 0 when the device was already dead and nothing
        /// was submitted.</returns>
        /// <exception cref="InvalidOperationException">The list is not sealed, the fence is already armed, or the
        /// submit failed with a non-loss result.</exception>
        internal ulong Submit(VulkanCommandList list, VulkanGpuFence? fence)
        {
            ArgumentNullException.ThrowIfNull(list);

            // Read OUTSIDE the lock, and it is the sealed-record check rather than a race: a list is one thread's
            // at a time, so a caller that has not sealed one is refused before it can contend for the lock.
            ulong buffer = list.SealedBuffer;

            // A DEAD DEVICE SUBMITS NOTHING AND SAYS SO ONCE. Every native call against a destroyed or lost device
            // aborts the process through the Vulkan loader, and the loss itself was already latched, logged and
            // put in the telemetry session header at the site that noticed it. Returning is the same posture
            // WaitForIdle and every Dispose on this backend take (V-F10): after death, quiet and safe answers.
            if (_timeline.IsDeviceDead) return 0;

            lock (_submitLock)
            {
                // FIRST STATEMENT IN THE LOCK. See the class note: this is the whole of the ordering guarantee.
                ulong value = _timeline.NextSubmitValue();

                // Armed BEFORE the submit, so a fence that is still armed from an earlier submission throws here
                // rather than after work has been queued against it. The value it takes is unregistered until the
                // submit succeeds, and the failure path below unarms it again.
                fence?.Arm(value);

                VulkanSubmitStatus status = _api.Submit(buffer, value, out string? failure);

                if (status == VulkanSubmitStatus.Success)
                {
                    // ONLY HERE does the drain's target move. Everything above this line is reversible and
                    // everything below it is a fact about the queue.
                    _timeline.RegisterSubmitted(value);
                    list.RecordSubmitted(value);
                    return value;
                }

                fence?.Reset();

                if (status == VulkanSubmitStatus.DeviceLost)
                {
                    // Latched, logged and headered at the submit's own site. Nothing more to say and nothing to
                    // throw: a lost device is not a failure this call can be retried past.
                    return 0;
                }

                _log.Error($"A native Vulkan vkQueueSubmit failed with {failure}. Timeline value {value} was "
                    + "allocated to it and will never be signalled, which is why the drain target was not raised "
                    + $"to it: WaitForIdle still waits for value {_timeline.LastSubmitted}, which the GPU can "
                    + "still reach.");

                throw new InvalidOperationException(
                    $"The native Vulkan backend's vkQueueSubmit failed: {failure}. The command buffer was NOT "
                    + "queued and its work will not run. The device timeline is unharmed: the value this "
                    + "submission took is never signalled and never waited for, so WaitForIdle still terminates "
                    + "and every outstanding fence still resolves. Both results that reach here mean the process "
                    + "or the device is out of memory.");
            }
        }
    }
}
