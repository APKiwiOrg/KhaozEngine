using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// HOW THE ALLOCATOR HANDS A CHUNK'S <c>vkFreeMemory</c> TO THE DEFERRED-DISPOSAL PATH (V-F9), without
    /// learning the timeline or the retire list.
    /// <para>
    /// Returning a chunk's memory to the driver is exactly the operation V-F9 exists for: a submission recorded
    /// before the free can still be reading the buffer or the image bound into that chunk, and freeing the memory
    /// underneath it is a use-after-free the GPU finds rather than the CPU. So the allocator never calls
    /// <c>vkFreeMemory</c> at the moment a chunk empties. It hands the destroy here, and here records the
    /// device's current timeline value with it, and the retire list runs it once the counter has passed.
    /// </para>
    /// <para>
    /// AN INTERFACE BECAUSE THE ALLOCATOR IS DEVICE-FREE. A test drives the whole retire ordering (a chunk freed,
    /// nothing destroyed, the timeline advanced, the destroy running) with a fake, which is the only way to assert
    /// that ordering at all before row 7 makes a real submission possible.
    /// </para>
    /// </summary>
    internal interface IVulkanMemoryRetirement
    {
        /// <summary>Hold <paramref name="destroy"/> until every submission that could still be reading the memory
        /// it frees has completed.</summary>
        void Retire(Action destroy);
    }

    /// <summary>
    /// The real hook: record <see cref="VulkanTimeline.LastAllocated"/> with the destroy and hand it to the
    /// device's <see cref="VulkanRetireList"/>.
    /// <para>
    /// <c>LastAllocated</c> is the right value for the reason the retire list's own documentation gives: it is the
    /// last value allocated to a submission, so every submission that could have referenced this memory has
    /// already been made, and a counter that has reached it has finished all of them. Before anything has been
    /// allocated it is 0, and an entry at 0 is released by the very next drain, which is correct rather than a
    /// special case.
    /// </para>
    /// <para>
    /// NOT <c>LastSubmitted</c>, WHICH IS THE SMALLER OF THE TWO. A submission that has taken its value and whose
    /// <c>vkQueueSubmit</c> has not returned yet is already able to reference this memory and is not yet in the
    /// registered high-water, so gating on that number would free a chunk underneath a submission in flight. The
    /// allocation high-water cannot be too small, only too conservative, and a gap left by a failed submit is
    /// stepped over by the next successful signal rather than stranding the entry.
    /// </para>
    /// </summary>
    internal sealed class VulkanTimelineRetirement : IVulkanMemoryRetirement
    {
        readonly VulkanTimeline _timeline;
        readonly VulkanRetireList _retired;

        /// <param name="timeline">The device's one completion timeline.</param>
        /// <param name="retired">The device's deferred-disposal list.</param>
        internal VulkanTimelineRetirement(VulkanTimeline timeline, VulkanRetireList retired)
        {
            ArgumentNullException.ThrowIfNull(timeline);
            ArgumentNullException.ThrowIfNull(retired);

            _timeline = timeline;
            _retired = retired;
        }

        /// <inheritdoc/>
        public void Retire(Action destroy) => _retired.Retire(_timeline.LastAllocated, destroy);
    }
}
