using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// WHAT EVERY RESOURCE NEEDS FROM ITS DEVICE, in one object, so a wrapper's constructor names four things
    /// instead of taking four parameters that must always travel together and must always be the SAME four.
    ///
    /// <para><b>A SECOND ALLOCATOR OR A SECOND RETIRE LIST WOULD BE A SILENT SPLIT.</b> The device owns exactly one
    /// of each, and a resource created against a different one would free memory into a pool nobody drains and
    /// retire a destroy nobody runs. Bundling them removes the shape of that mistake from every signature that
    /// would otherwise carry them apart.</para>
    ///
    /// <para><b>IT DELIBERATELY DOES NOT CARRY THE DEVICE.</b> Reaching the device from a wrapper is the reference
    /// cycle the incumbent did not have either, and it is what would let a resource call back into a lifecycle it
    /// is a child of.</para>
    /// </summary>
    /// <param name="Api">The twelve native resource calls.</param>
    /// <param name="Memory">The device's ONE block suballocator (V-M1).</param>
    /// <param name="Timeline">The device's ONE completion timeline, whose current value a deferred destroy is
    /// recorded at (V-F9).</param>
    /// <param name="Retired">The device's ONE deferred-disposal list.</param>
    internal sealed record VulkanResourceOwner(
        IVulkanResourceApi Api,
        VulkanMemoryAllocator Memory,
        VulkanTimeline Timeline,
        VulkanRetireList Retired)
    {
        /// <summary>
        /// HOLD ONE TERMINAL DESTROY behind the timeline (V-F9): record the value the device has handed out most
        /// recently, and run <paramref name="destroy"/> once the GPU has passed it.
        ///
        /// <para><b>TERMINAL IS THE WHOLE CONTRACT, AND THIS BACKEND CHOSE IT DELIBERATELY.</b> A resource's
        /// destroy is ONE entry that ends every native object the resource owns INLINE (a texture's image views and
        /// then its image, a buffer's buffer) and then frees its allocation. It never RE-RETIRES a child, which is
        /// the alternative the review of row 6 named: a destroy that retired another destroy that then freed an
        /// allocation would append a third generation of entries after the teardown drain had already taken its
        /// snapshot, and that chunk would never be freed. Freeing the allocation may still retire the CHUNK it came
        /// out of, which is one further generation and exactly the one the device's teardown already drains twice
        /// for. So the depth is bounded at two by construction rather than by a loop with a guard on it.</para>
        ///
        /// <para><b>THE VALUE IS THE LAST ALLOCATED ONE, NOT THE LAST SUBMITTED.</b> A submission in flight between
        /// taking its value and registering it has not raised the submitted high-water yet, and a destroy gated on
        /// the lower number would run underneath it. The allocated value is at or above every value any live
        /// submission can hold.</para>
        /// </summary>
        internal void RetireTerminal(Action destroy)
        {
            ArgumentNullException.ThrowIfNull(destroy);
            Retired.Retire(Timeline.LastAllocated, destroy);
        }
    }
}
