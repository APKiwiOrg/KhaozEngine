using System;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// ONE IMAGE LAYOUT TRANSITION AS A <c>vkCmdPipelineBarrier2</c> BARRIER (V-F6), and the total function over
    /// layouts it is built from. Pure arithmetic over enums, so what a transition synchronises against is a plain
    /// <c>[Fact]</c> rather than a thing only a validation layer on a real device can see.
    ///
    /// <para><b>ARMS OVER LAYOUTS, NOT OVER LAYOUT PAIRS, AND THAT IS THE WHOLE OF THE DECISION.</b> The incumbent
    /// answers a transition with a 25-arm if/else over the PAIR of layouts, ending in a debug assertion, and in
    /// Release it silently emits <c>NONE</c> on both stage masks for a pair it does not handle. A barrier with
    /// <c>NONE</c> on both sides synchronises nothing, and its only signal is an assertion that compiles away. Here
    /// each SIDE is answered independently by <see cref="StageFor"/> and <see cref="AccessFor"/>, which are total
    /// over the eight layouts this backend uses, so every pair is covered by construction and there is no
    /// unhandled one to fall through. A layout outside the eight throws by name instead of emitting an empty
    /// mask.</para>
    ///
    /// <para><b>THE SOURCE SIDE IS THE OLD LAYOUT'S ACCESS AND THE DESTINATION SIDE IS THE NEW LAYOUT'S.</b> That
    /// is what a layout means: an image in <c>COLOR_ATTACHMENT_OPTIMAL</c> is being read and written by colour
    /// attachment output, and one in <c>SHADER_READ_ONLY_OPTIMAL</c> is being sampled. So a transition out of the
    /// first and into the second makes the attachment writes available and the sampled reads visible, which is
    /// exactly the barrier the design's table asks for at a post-chain boundary.</para>
    ///
    /// <para><b><c>UNDEFINED</c> IS REFUSED HERE (V-F8), WHICH IS WHERE THE DETERMINISM RULE BECOMES
    /// MECHANICAL.</b> A transition whose old layout is <c>UNDEFINED</c> is permitted to DISCARD the image's
    /// contents, so it is the cheap transition and the tempting one, and using it on an image whose contents are
    /// still wanted produces output that varies by driver and by run while the goldens require stability on the
    /// same rasterizer. It appears as an old layout in exactly two places in the backend: a texture's first-ever
    /// transition, which is <see cref="VulkanSetupBarrier.FirstUse"/> on the setup buffer, and a swapchain image
    /// being reacquired for a frame that will fully overwrite it, which is <see cref="Reacquired"/> below.
    /// <see cref="For"/> refuses it, so a third site cannot be written by accident.</para>
    ///
    /// <para><b>AND AS A NEW LAYOUT IT IS REFUSED ON EVERY PATH, WHICH IS A DIFFERENT RULE WITH A DIFFERENT
    /// REASON.</b> VUID-VkImageMemoryBarrier2-newLayout-01198 forbids <c>UNDEFINED</c> as a destination outright:
    /// it is the state an image is in before anything has happened to it rather than one anything can be moved
    /// into, and a barrier naming it leaves the image unusable to every later command in the recording. So that
    /// refusal sits in the one constructor BOTH entry points pass through rather than in <see cref="For"/> alone,
    /// because there is no legitimate site for it at all rather than two.</para>
    ///
    /// <para><b><c>PRESENT_SRC_KHR</c> IS A DESTINATION AND NEVER A SOURCE.</b> An image handed to
    /// <c>vkQueuePresentKHR</c> is next seen through an acquire, and the acquire's transition discards through
    /// <see cref="Reacquired"/> rather than reading the presented contents back, so the presented layout is never
    /// the old half of a pair. Its destination masks are the bottom of the pipe with no access at all, because the
    /// presentation engine is not a pipeline stage and the ordering that matters is carried by the present
    /// semaphore rather than by this barrier.</para>
    /// </summary>
    internal static unsafe class VulkanImageTransition
    {
        /// <summary>
        /// The pipeline stages that touch an image in <paramref name="layout"/>. Used as the SOURCE stage for the
        /// layout being left and the DESTINATION stage for the layout being entered.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">A layout outside the eight this backend uses. Answering
        /// <c>NONE</c> instead would be the incumbent's Release-build behaviour, which is a barrier that
        /// synchronises nothing.</exception>
        internal static PipelineStageFlags2 StageFor(ImageLayout layout) => layout switch
        {
            // NOTHING HAS HAPPENED TO IT. Only ever a source, and only at the two sites named in the type remarks.
            ImageLayout.Undefined => PipelineStageFlags2.TopOfPipeBit,

            // EVERY STAGE, because GENERAL is the layout that permits every access and a storage image resting in
            // it can have been touched by anything. Narrowing this would need to know what last wrote it, which is
            // information a layout alone does not carry.
            ImageLayout.General => PipelineStageFlags2.AllCommandsBit,

            // ALL THREE PROGRAMMABLE STAGES, for the reason VulkanUploadBarrier names on the buffer side: the seam
            // carries no stage visibility on a texture's usage, so vertex, fragment and compute is the
            // conservative answer that is still far narrower than a whole-pipeline flush.
            ImageLayout.ShaderReadOnlyOptimal => PipelineStageFlags2.VertexShaderBit
                | PipelineStageFlags2.FragmentShaderBit
                | PipelineStageFlags2.ComputeShaderBit,

            ImageLayout.ColorAttachmentOptimal => PipelineStageFlags2.ColorAttachmentOutputBit,

            // BOTH DEPTH TEST STAGES, because a depth attachment is read and written at either depending on
            // whether the pipeline declares early fragment tests, and the barrier cannot see which.
            ImageLayout.DepthStencilAttachmentOptimal => PipelineStageFlags2.EarlyFragmentTestsBit
                | PipelineStageFlags2.LateFragmentTestsBit,

            ImageLayout.TransferSrcOptimal => PipelineStageFlags2.AllTransferBit,
            ImageLayout.TransferDstOptimal => PipelineStageFlags2.AllTransferBit,

            // A DESTINATION ONLY, and a destination that waits for nothing: see the type remarks.
            ImageLayout.PresentSrcKhr => PipelineStageFlags2.BottomOfPipeBit,

            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, UnknownLayout),
        };

        /// <summary>
        /// The accesses an image in <paramref name="layout"/> is touched through. Used as the SOURCE access for the
        /// layout being left and the DESTINATION access for the layout being entered.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">A layout outside the eight this backend uses.</exception>
        internal static AccessFlags2 AccessFor(ImageLayout layout) => layout switch
        {
            // NO ACCESS TO MAKE AVAILABLE, because none can have happened. A source mask over accesses that cannot
            // have occurred orders nothing and reads as though it did.
            ImageLayout.Undefined => AccessFlags2.None,

            ImageLayout.General => AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,

            // SAMPLED READ ALONE. SHADER_READ_ONLY_OPTIMAL permits no write at all, so naming one would describe an
            // access the layout forbids.
            ImageLayout.ShaderReadOnlyOptimal => AccessFlags2.ShaderSampledReadBit,

            ImageLayout.ColorAttachmentOptimal => AccessFlags2.ColorAttachmentReadBit
                | AccessFlags2.ColorAttachmentWriteBit,

            ImageLayout.DepthStencilAttachmentOptimal => AccessFlags2.DepthStencilAttachmentReadBit
                | AccessFlags2.DepthStencilAttachmentWriteBit,

            ImageLayout.TransferSrcOptimal => AccessFlags2.TransferReadBit,
            ImageLayout.TransferDstOptimal => AccessFlags2.TransferWriteBit,

            // NONE, deliberately, and it is the one place an empty access mask is right: the presentation engine
            // performs no pipeline access, and the present semaphore carries the ordering.
            ImageLayout.PresentSrcKhr => AccessFlags2.None,

            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, UnknownLayout),
        };

        /// <summary>
        /// The barrier that moves <paramref name="image"/>'s <paramref name="range"/> from
        /// <paramref name="oldLayout"/> to <paramref name="newLayout"/>, with both stage masks and both access
        /// masks named explicitly.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="oldLayout"/> is <c>UNDEFINED</c>, which would
        /// discard the image's contents. See the type remarks for the two sites permitted to do that and for why
        /// this is a determinism rule rather than a nicety. Also when <paramref name="newLayout"/> is
        /// <c>UNDEFINED</c>, which no site may name (VUID-VkImageMemoryBarrier2-newLayout-01198).</exception>
        internal static ImageMemoryBarrier2 For(ulong image, in ImageSubresourceRange range, ImageLayout oldLayout,
            ImageLayout newLayout)
        {
            if (oldLayout == ImageLayout.Undefined)
            {
                throw new ArgumentException(
                    "A native Vulkan layout transition out of VK_IMAGE_LAYOUT_UNDEFINED DISCARDS the image's "
                    + "contents, and it is permitted in exactly two places in this backend (V-F8): a texture's "
                    + "first-ever transition, on the device's setup command buffer, and a swapchain image being "
                    + "reacquired for a frame that will fully overwrite it. Both have their own named entry point. "
                    + "A discard anywhere else produces output that varies by driver and by run, which the goldens "
                    + "cannot tolerate, and it does not throw or render obviously wrong when it happens.",
                    nameof(oldLayout));
            }

            return Barrier(image, range, oldLayout, newLayout,
                StageFor(oldLayout), AccessFor(oldLayout),
                StageFor(newLayout), AccessFor(newLayout));
        }

        /// <summary>
        /// THE SECOND AND LAST SITE PERMITTED TO NAME <c>UNDEFINED</c> AS AN OLD LAYOUT (V-F8): a swapchain image
        /// reacquired for a frame that will fully overwrite it. The first is
        /// <see cref="VulkanSetupBarrier.FirstUse"/>, which is a texture created microseconds ago and has no
        /// contents to lose.
        /// <para>
        /// IT IS A NAMED ENTRY POINT RATHER THAN A FLAG ON <see cref="For"/>, so the discard cannot be reached by
        /// passing a value and the two legitimate sites are both greppable.
        /// </para>
        /// </summary>
        /// <param name="image">The reacquired swapchain <c>VkImage</c>.</param>
        /// <param name="range">Its whole subresource range, which on a swapchain image is one mip and one
        /// layer.</param>
        /// <param name="newLayout">What the frame will use it as, which is its attachment layout.</param>
        /// <exception cref="ArgumentException"><paramref name="newLayout"/> is <c>UNDEFINED</c>. The discard this
        /// entry point exists for is on the OLD side, and a barrier from <c>UNDEFINED</c> to <c>UNDEFINED</c> is
        /// not a cheap acquire but an invalid barrier (VUID-VkImageMemoryBarrier2-newLayout-01198).</exception>
        internal static ImageMemoryBarrier2 Reacquired(ulong image, in ImageSubresourceRange range,
            ImageLayout newLayout)
            => Barrier(image, range, ImageLayout.Undefined, newLayout,
                StageFor(ImageLayout.Undefined), AccessFor(ImageLayout.Undefined),
                StageFor(newLayout), AccessFor(newLayout));

        // The one constructor, so the six fields that must always be set together cannot be set apart. No queue
        // family transfer anywhere: this backend creates ONE queue on ONE family (V-N5), so both indexes are
        // IGNORED and an ownership transfer is not expressible. Same shape VulkanSetupBarrier's own constructor
        // takes, and deliberately not shared with it: that type carries the setup buffer's three fixed
        // transitions with their own conservative masks, and this one is the general per-list model.
        //
        // THE NEW-LAYOUT REFUSAL LIVES HERE RATHER THAN IN For, because it is a rule about the BARRIER and not
        // about one entry point: both of the sites permitted to name UNDEFINED as an OLD layout pass through
        // here too, and neither may name it as the new one.
        static ImageMemoryBarrier2 Barrier(ulong image, in ImageSubresourceRange range, ImageLayout oldLayout,
            ImageLayout newLayout, PipelineStageFlags2 srcStage, AccessFlags2 srcAccess,
            PipelineStageFlags2 dstStage, AccessFlags2 dstAccess)
        {
            if (newLayout == ImageLayout.Undefined) throw new ArgumentException(UndefinedDestination,
                nameof(newLayout));

            return new(
                sType: StructureType.ImageMemoryBarrier2,
                srcStageMask: srcStage,
                srcAccessMask: srcAccess,
                dstStageMask: dstStage,
                dstAccessMask: dstAccess,
                oldLayout: oldLayout,
                newLayout: newLayout,
                srcQueueFamilyIndex: Vk.QueueFamilyIgnored,
                dstQueueFamilyIndex: Vk.QueueFamilyIgnored,
                image: new Image(image),
                subresourceRange: range);
        }

        const string UndefinedDestination =
            "A native Vulkan layout transition INTO VK_IMAGE_LAYOUT_UNDEFINED is invalid: "
            + "VUID-VkImageMemoryBarrier2-newLayout-01198 forbids it outright, because UNDEFINED is what an image "
            + "is before anything has happened to it rather than a state anything can be moved into. It is not "
            + "the mirror of the old-layout rule and it is not a discard: a barrier that names it makes the image "
            + "unusable to every later command in the recording, and the transition it was meant to be is the "
            + "resting-layout restore at End (V-F7). The validation layer reports this one, so it is caught on a "
            + "machine that has the layer and silent on every machine that does not.";

        const string UnknownLayout =
            "A native Vulkan image layout outside the eight this backend uses. Every barrier names both stage "
            + "masks and both access masks explicitly (V-F6), so an unrecognised layout is refused rather than "
            + "answered with an empty mask: an empty mask on both sides is a barrier that synchronises nothing, "
            + "which is what the incumbent emits in Release for a layout pair its if/else does not handle.";
    }
}
