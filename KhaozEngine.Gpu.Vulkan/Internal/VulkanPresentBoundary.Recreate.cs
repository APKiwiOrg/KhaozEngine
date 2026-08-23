using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    // THE RECREATION STATE MACHINE, the half of the present boundary that runs when the swapchain the boundary is
    // holding stops being the one the surface wants: deciding a spec from what the surface reports, creating the
    // generation, and falling to the orphan target when the surface cannot be read or what it says cannot be
    // turned into a swapchain. The per-frame boundary itself (present, acquire, publish) stays in
    // VulkanPresentBoundary.cs, and the two refusal factories live here because the first-generation path is the
    // only one that throws them. Same partial type, so the boundary calls straight in with no seam.
    //
    // Split out at 799 of the 800-line cap (#559), on the seam the file already marked, so the next feature in
    // either half has somewhere to land.
    internal sealed partial class VulkanPresentBoundary
    {
        void Recreate(bool firstGeneration)
        {
            if (!TryDecide(firstGeneration, out VulkanSwapchainSpec spec))
            {
                // THE SURFACE COULD NOT BE READ, OR WHAT IT SAID CANNOT BE TURNED INTO A SWAPCHAIN. There is
                // nothing to create against and nothing to throw at, so the frame goes on the orphan target at
                // the last size the framebuffer carried.
                FallToOrphan(LastKnownExtent());
                return;
            }

            // A ZERO-EXTENT SURFACE, which is what a minimised window reports and what the extent clamp turns into
            // a spec that reads as not creatable. There is nothing to call vkCreateSwapchainKHR with.
            if (!spec.IsCreatable)
            {
                FallToOrphan(spec.Extent.AtLeastOnePixel);
                return;
            }

            string? failure;
            lock (_submitLock)
            {
                // UNCONDITIONAL, and that is the whole retirement rule (V-W6). A binary semaphore an acquire or a
                // submit signalled that nothing waited on is left PENDING, there is no way to ask one whether it
                // is, and destroying a pending semaphore is undefined behaviour drivers mostly tolerate until they
                // do not. A drained queue is the only state in which the answer is knowable.
                _drain();

                VulkanSwapchainGeneration? made = VulkanSwapchainGeneration.TryCreate(
                    _swapchains, _surface, spec, _generation?.Handle ?? 0, out failure);

                if (made is not null)
                {
                    Adopt(made, imageIndex: 0);
                    RetireGeneration();
                    _generation = made;
                    ForgetHeldImage();

                    if (_mode == VulkanAcquireMode.Semaphore) _ring.Rebuild(made.ImageCount);

                    // The orphan is released only once a real image is bound again, which is the next successful
                    // acquire. Doing it here would destroy the image the framebuffer is pointing at until one
                    // statement ago.
                    return;
                }
            }

            // A FAILED CREATION TAKES THE OLD GENERATION WITH IT, and that is a specification fact rather than a
            // policy choice here. vkCreateSwapchainKHR RETIRES the swapchain handed to it as oldSwapchain whether
            // or not it succeeds, and a retired swapchain may already have had the images nothing acquired freed
            // underneath it. Keeping the old generation therefore does not keep a framebuffer pointing at views
            // that are still alive: it keeps one pointing at images the driver may have taken back, and it hands
            // the same now-retired handle to the next attempt, which the specification forbids
            // (VUID-VkSwapchainCreateInfoKHR-oldSwapchain-01933). So the old generation is retired here and the
            // frame binds the orphan target, exactly as a zero-extent surface does.
            //
            // THE FIRST GENERATION REFUSES BEFORE ANY OF THAT IS SAID, because there is no old generation to
            // retire and no frame to bind anything for: that is the device constructor, and its own message is
            // the whole of what a reader needs.
            if (firstGeneration) throw NoFirstSwapchain(spec, failure);

            _log.Warn("The native Vulkan backend could not create a swapchain at "
                + spec.Extent.Width + "x" + spec.Extent.Height + ": " + (failure ?? "no reason reported")
                + ". vkCreateSwapchainKHR retires the old swapchain even when it fails, so the previous "
                + "generation is retired here rather than kept, the frame binds the orphan target, and the "
                + "recreate is retried at the next present boundary with no old swapchain to pass.");
            _pending.QueueRecreate();

            FallToOrphan(spec.Extent.AtLeastOnePixel);
        }

        /// <summary>
        /// WHAT THE SURFACE SAYS AND WHAT THE POLICY MAKES OF IT, with every failure routed exactly the way an
        /// acquire's or a present's is. Answers false when there is nothing to create against, having already
        /// logged and latched whatever it found.
        /// <para>
        /// THIS EXISTS BECAUSE ALL THREE OF THESE USED TO THROW OUT OF <c>IGpuDevice.Present</c>, which is a
        /// contradiction of this type's first sentence: the capability query through
        /// <c>VulkanResultCodes.Require</c>, <c>ChooseFormat</c> on an empty format list, and
        /// <c>SeamFormatFor</c> on a format the seam cannot name. The first is the one that matters most, because
        /// <c>VK_ERROR_SURFACE_LOST_KHR</c> shows up THERE first when a window dies under a running frame loop,
        /// and the boundary had a surface-lost path for the acquire and the present and none for the query that
        /// runs before both.
        /// </para>
        /// <para>
        /// THE FIRST GENERATION STILL THROWS, on all three, because it is the device constructor rather than a
        /// frame boundary: a windowed device that cannot describe its own surface has nothing to hand back, and
        /// refusing at creation is what the failed-first-swapchain path already does.
        /// </para>
        /// </summary>
        bool TryDecide(bool firstGeneration, out VulkanSwapchainSpec spec)
        {
            spec = default;

            VulkanPresentOutcome queried = _surfaces.Query(_surface, out VulkanSurfaceReport report);
            if (queried != VulkanPresentOutcome.Success)
            {
                if (firstGeneration) throw NoFirstSurface(queried.ToString());

                // THE SAME DISCIPLINE THE ACQUIRE AND THE PRESENT GET (V-W7), through the same method: a lost
                // surface latches and stops the boundary rather than spinning on a recreate that cannot succeed,
                // and anything else queues a recreate and tries again at the next boundary.
                Interpret(queried, "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
                return false;
            }

            if (report.Formats.Count == 0)
            {
                // READ AS A FAILED QUERY RATHER THAN AS AN EMPTY ANSWER. The specification requires a surface the
                // device can present to to report at least one format, and the surface seam answers an empty list
                // on ANY failed format query, so there is no case where this is a surface that simply has none.
                if (firstGeneration) throw NoFirstSurface("vkGetPhysicalDeviceSurfaceFormatsKHR reported no format");

                SayUndecidableOnce("the surface reports no format at all, which means its format query failed");
                _pending.QueueRecreate();
                return false;
            }

            spec = VulkanSwapchainPolicy.Decide(
                report, _requested, _syncToVerticalBlank, srgb: false, out string? warning);

            if (warning != null) _log.Warn(warning);

            try
            {
                // RESOLVED BEFORE ANYTHING IS CREATED, so the orphan and the swapchain framebuffer always agree
                // about the format a pipeline is validated against, on every path including the one where no
                // swapchain is ever made.
                _seamFormat = VulkanSwapchainPolicy.SeamFormatFor(spec.Format);
            }
            catch (NotSupportedException unnameable)
            {
                // CAUGHT RATHER THAN LEFT TO ESCAPE, because it is REACHABLE rather than unreachable by
                // construction: ChooseFormat's last arm takes the surface's FIRST format when the surface offers
                // no BGRA8 at all, and that can be any format the surface happens to have.
                if (firstGeneration) throw;

                SayUndecidableOnce(unnameable.Message);
                _pending.QueueRecreate();
                spec = default;
                return false;
            }

            return true;
        }

        // THE IMAGELESS TARGET, and the ONE way this boundary ever ends up with no generation: a zero-extent
        // surface, a surface that could not be read or described, and a creation that failed after the driver had
        // already retired the old swapchain. All three leave the framebuffer on the orphan, the generation retired
        // and the frame's semaphores forgotten.
        void FallToOrphan(VulkanExtent extent)
        {
            // ENSURED BEFORE THE LOCK, because creating a texture takes the SETUP lock and the setup lock is taken
            // before the submit lock and never after it (V-W8).
            VulkanAttachment attachment = _orphan.Ensure(extent, _seamFormat);

            lock (_submitLock)
            {
                // UNCONDITIONAL AND BEFORE THE RETIREMENT, for the reason the creating path's is.
                _drain();

                // PUBLISHED BEFORE THE OLD VIEWS DIE, which is the ordering rule that makes a use-after-free
                // unreachable rather than merely unlikely. A minimised window reaches exactly this.
                AdoptOrphan(attachment, extent);
                RetireGeneration();
                ForgetHeldImage();
            }

            // THE ACQUIRE RING IS LEFT EXACTLY AS IT IS, which is what the creatable path does NOT do and is
            // deliberate rather than an omission. A ring semaphore is only ever handed out by an acquire, an
            // acquire needs a generation, and there is none now, so nothing can reach a semaphore an earlier
            // acquire left signalled. The next successful creation rebuilds the whole set inside the lock before
            // any acquire can run. Rebuilding here instead would destroy the set while the queue has just drained
            // for no reason, on the one path where the device is already in trouble.
        }

        static InvalidOperationException NoFirstSurface(string reason)
            => new("The native Vulkan backend could not describe the surface it was asked to present to: "
                + reason + ". A windowed device whose surface cannot be read has nothing to create a swapchain "
                + "against, so creation fails here rather than handing back a device that renders into a window "
                + "that never updates. This is a driver or window-system fault rather than a backend choice: "
                + "since 18.0.0 the native backend is the only Vulkan implementation the engine has, so there is "
                + "nothing else to select. A headless device (GpuDeviceContext.CreateHeadless) needs no surface "
                + "and still works, and on Windows or macOS that platform's own native backend is the supported "
                + "windowed path.");

        static InvalidOperationException NoFirstSwapchain(in VulkanSwapchainSpec spec, string? failure)
            => new("The native Vulkan backend could not create its FIRST swapchain at "
                + spec.Extent.Width + "x" + spec.Extent.Height + ": " + (failure ?? "no reason reported")
                + ". A windowed device with no swapchain has nothing to present to, so creation fails here rather "
                + "than handing back a device that renders into a window that never updates. This is a driver "
                + "or window-system fault rather than a backend choice: since 18.0.0 the native backend is the "
                + "only Vulkan implementation the engine has, so there is nothing else to select. A headless "
                + "device (GpuDeviceContext.CreateHeadless) needs no swapchain and still works, and on Windows "
                + "or macOS that platform's own native backend is the supported windowed path.");
    }
}
