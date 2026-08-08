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
        /// REACHED THROUGH THE SEAM AS <c>IGpuResourceFactory.CreateCommandList</c>, which row 9
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/519) wired to this member. It stays internal as well
        /// as seam-reachable for the reason <see cref="Timeline"/> is: it is a device-owned primitive later rows
        /// have to be able to assume exists.
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

            var ring = new VulkanCommandPoolRing(_commands, _framesInFlight, _timeline, _backpressure);

            // ITS OWN STAGING ARENA (V-M9, 9.3), on the device's ONE staging source so every block on the device
            // comes out of the same allocator and every block's destroy is deferred behind the same timeline. The
            // arena is PER LIST because its recycling boundary is the list's own slot wait, which is the one proof
            // available that the blocks it hands back are finished with.
            //
            // THE RENDERING SCOPE IS DELIBERATELY NOT WIRED HERE. The list below takes this uploader and hands
            // ITSELF back as the scope from its own constructor, which is what makes a bulk upload's pass-end
            // (V-A4) reachable on every list rather than on the ones a caller remembered to finish wiring. See
            // IVulkanRecordUploads.UseRenderingScope.
            var uploads = new VulkanListUploads(
                _instance.Value.Api, ring, new VulkanStagingArena(_staging, _framesInFlight));

            // DECISION V-R7's DRAW-TIME HALF FOLLOWS THE SAME LEVER THE LAYER ITSELF DOES. The assertion that every
            // bound set's layout is the pipeline layout's set layout at that index is a per-bind loop, so it is
            // armed exactly when KE_VULKAN_VALIDATION armed the layer, read off the instance this device leased
            // rather than off the environment a second time: two reads could disagree if the variable moved
            // mid-process, and a device whose lists disagree with its own instance about validation is the worst
            // available answer.
            bool assertBoundSetLayouts = _instance.Value.Validation != VulkanValidationMode.Off;

            // ROW 12's SIX RENDERING CALLS (https://github.com/APKiwiOrg/KhaozEngine/issues/522), which are
            // stateless: the seam is one Vk reference and the whole deferred-begin schedule sits above it inside
            // the list. One instance per list rather than one per device only because the list constructs its own
            // schedule from it, and neither object holds anything a second list could disturb.
            var render = new VulkanRenderApi(_instance.Value.Api);

            // ROW 13's ONE RECORD-TIME CALL (https://github.com/APKiwiOrg/KhaozEngine/issues/523), stateless for
            // the same reason and built per list for the same reason. It is NOT the device's _pipelines: that one
            // can create a pipeline and this one can only bind one, which is what keeps a shader compile off every
            // path a recorder can reach.
            var bindPipeline = new VulkanPipelineBinder(_instance.Value.Api);

            return new VulkanCommandList(ring, _retired, uploads, assertBoundSetLayouts, render, bindPipeline);
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
        public void Submit(IGpuCommandList cl)
        {
            VulkanCommandList list = Recording(cl);

            // THE SETUP BUFFER GOES FIRST (V-M10), so the creation-time clears and transitions of everything made
            // since the last flush execute BEFORE the frame that reads them. Two sequential lock acquisitions
            // rather than a nested pair: this returns having released the setup lock before the submit below takes
            // the submit lock, which is what keeps the two locks a strict order rather than a cycle.
            FlushSetup();

            _submits.Submit(list, null, TakeFrameSemaphores());
        }

        // THE SWAPCHAIN'S BINARY PAIR FOR THIS FRAME, taken by the FIRST submit after an acquire and default for
        // every other one (V-W3). Default on every headless device, which has no swapchain at all, and default
        // under KE_VULKAN_ACQUIRE=stall, which reproduces the incumbent's semaphore-free submit exactly.
        //
        // ONCE PER FRAME IS THE CONTRACT AND IT IS A HANG IF IT IS BROKEN. A binary semaphore may be waited once
        // per signal, so a second submit in one frame carrying the same wait semaphore waits for a signal nothing
        // will ever produce. The boundary enforces the once UNDER THE DEVICE'S SUBMIT LOCK rather than by
        // assuming one submitting thread, because this seam nowhere says Submit is single-threaded and V-W8 says
        // recording is lock-free and per-list on any number of threads. This call is still made outside the lock
        // the submit queue takes below, which is harmless now that the take itself is atomic: the pair goes to
        // whichever submit reaches it first and every other one gets the default.
        VulkanFrameSemaphores TakeFrameSemaphores() => _present?.TakeFrameSemaphores() ?? default;

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

            VulkanCommandList list = Recording(cl);

            // Same order and same reason as the fenceless overload.
            FlushSetup();

            _submits.Submit(list, armed, TakeFrameSemaphores());
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
