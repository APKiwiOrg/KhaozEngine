using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE RECORDING AND SUBMISSION HALF OF THE NATIVE DEVICE: the command lists it hands out, and the one
    /// <c>vkQueueSubmit</c> per submission that puts them on the queue. Work-breakdown row 7
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/517), split from the seam surface for the same reason
    /// creation is, and because a device that must stay under the file-size cap has room for none of the three.
    /// <para>
    /// The ORDERING lives in <see cref="VulkanSubmitQueue"/> and not here, so all of it is device-free and driven
    /// by a plain <c>[Fact]</c>. What is left in this file is the seam's own two overloads, the argument checks
    /// that turn a foreign list or a foreign fence into a named refusal, and the wiring that gives every list on
    /// this device the same depth, the same retire list and the same backpressure accumulator.
    /// </para>
    /// <para>
    /// THE CHECKS REFUSE BY TYPE, NOT BY DEVICE. A list or fence made by another GPU BACKEND is caught, but one
    /// made by a DIFFERENT native Vulkan device would pass the same cast, because nothing here compares device
    /// identity. Running two native Vulkan devices in one process is not a shape this backend ships today
    /// though: the lifecycle gate serialises creation and nothing constructs a second one.
    /// </para>
    /// </summary>
    internal sealed unsafe partial class VulkanGpuDevice
    {
        /// <summary>
        /// A COMMAND LIST OF THIS DEVICE'S: its own <see cref="VulkanFramesInFlight"/> command pools, one primary
        /// buffer each, gating on this device's timeline and stalling into this device's backpressure
        /// accumulator.
        /// <para>
        /// REACHED THROUGH THE SEAM AS <c>IGpuResourceFactory.CreateCommandList</c>, which is row 9's
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/519), so until that row lands this is how the rows
        /// built on recording get a list. Exposed for exactly the reason <see cref="Timeline"/> is: it is a
        /// device-owned primitive that later rows have to be able to assume exists.
        /// </para>
        /// <para>
        /// THE POOLS ARE CREATED HERE, at list creation, and never on a record path. A <c>Begin</c> that could
        /// allocate a driver object is a <c>Begin</c> that can fail on frame 4000, and every one of the 25
        /// <c>DEVICE_REMOVED</c> stacks in #423 came out of a lazy creation on the draw path on the other
        /// backend.
        /// </para>
        /// </summary>
        internal VulkanCommandList CreateCommandList()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return new VulkanCommandList(
                new VulkanCommandPoolRing(_commands, _framesInFlight, _timeline, _backpressure), _retired);
        }

        /// <summary>How many frames this device pipelines at (MV3), resolved once at creation from
        /// <c>KE_VULKAN_FRAMES_IN_FLIGHT</c>. Every command list cuts that many pool slots and, from row 8
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/518), every uniform ring cuts that many segments. One
        /// number, two different indexes off it: see <see cref="VulkanFramesInFlight"/>.</summary>
        internal int FramesInFlight => _framesInFlight;

        /// <summary>The device's ONE backpressure accumulator (MV3), which the command-list slot wait records into
        /// here and the uniform ring's segment gate will record into at row 8. Exposed because row 8 needs the
        /// same object rather than a second one: two accumulators would report a count nobody could compare
        /// against the exit criterion without adding them up first.</summary>
        internal VulkanBackpressure Backpressure => _backpressure;

        /// <inheritdoc/>
        /// <remarks>A submit with no fence STILL takes a timeline value, because the timeline has to advance with
        /// the submission stream for a later fence's value to cover the earlier work at all. That transitivity is
        /// what the deferred-disposal retire list is built on.</remarks>
        public void Submit(IGpuCommandList cl) => _submits.Submit(Recording(cl), null);

        /// <inheritdoc/>
        /// <remarks>
        /// ONE <c>vkQueueSubmit</c> (V-F3), signalling the fence's value through the timeline chained onto the
        /// submit info. The incumbent's second empty submit signalling an internal tracking fence is not
        /// inherited, and there is no <c>VkFence</c> anywhere in this backend: the fence handed in here is armed
        /// with the value this submission signals and answers by comparing it against the device's one counter.
        /// </remarks>
        public void Submit(IGpuCommandList cl, IGpuFence fence)
        {
            ArgumentNullException.ThrowIfNull(fence);

            if (fence is not VulkanGpuFence armed)
            {
                throw new ArgumentException(
                    "A fence created by another GPU backend was handed to the native Vulkan backend's Submit. A "
                    + $"fence on this backend is a value on this device's one timeline, and a {fence.GetType().Name} "
                    + "has no value on it. Create fences through the device you submit to.", nameof(fence));
            }

            _submits.Submit(Recording(cl), armed);
        }

        // The list check, shared by both overloads. A foreign list is refused by NAME rather than by a cast
        // exception, because the two ways to get here (a list from another device, or a list from the Veldrid
        // backend on a machine running both) look identical at the call site and neither is a bug in this backend.
        static VulkanCommandList Recording(IGpuCommandList cl)
        {
            ArgumentNullException.ThrowIfNull(cl);

            if (cl is not VulkanCommandList list)
            {
                throw new ArgumentException(
                    "A command list created by another GPU backend was handed to the native Vulkan backend's "
                    + $"Submit. A {cl.GetType().Name} holds no VkCommandBuffer, so there is nothing to queue. "
                    + "Create command lists from the device you submit them to.", nameof(cl));
            }

            return list;
        }
    }
}
