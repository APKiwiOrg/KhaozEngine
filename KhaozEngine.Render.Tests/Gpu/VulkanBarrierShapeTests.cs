using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE BARRIER SHAPE, WITH NO DEVICE (V-F6 and V-F8, section 10.3). Work-breakdown row 14
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/524).
    ///
    /// <para><b>THE REGRESSION THIS FILE EXISTS FOR IS A BARRIER THAT SYNCHRONISES NOTHING.</b> The incumbent
    /// answers a transition with a 25-arm if/else over the PAIR of layouts, ends it in a debug assertion, and in
    /// Release emits <c>NONE</c> on both stage masks for a pair it does not handle. That barrier orders nothing,
    /// renders correctly most of the time on most drivers, and its only signal is an assertion that compiles away.
    /// <see cref="EveryLayout_NamesAPipelineStage"/> is what actually forecloses it, one layout at a time, and
    /// <see cref="NoLayoutPair_ProducesAnEmptyMaskOnBothSides"/> then walks the whole pair space to show the
    /// composition does not lose the property. The second is IMPLIED by the first rather than independent of it,
    /// which is the point rather than a weakness: under a per-PAIR shape the same loop would be the only way to
    /// know, and it would be 49 separate facts.</para>
    ///
    /// <para><b>AND THE OTHER ONE IS A DISCARD NOBODY MEANT (V-F8).</b> A transition out of
    /// <c>VK_IMAGE_LAYOUT_UNDEFINED</c> is permitted to throw the image's contents away. It is the cheap
    /// transition and the tempting one, it does not throw and does not render obviously wrong, and it varies by
    /// driver and by run while the goldens require stability on the same rasterizer. Two sites in the backend are
    /// allowed to do it and both are named entry points.</para>
    /// </summary>
    public sealed class VulkanBarrierShapeTests
    {
        const ulong ImageHandle = 0xA11A5;

        /// <summary>Every layout this backend uses, which is what "total over the enum" means here.</summary>
        static IReadOnlyList<ImageLayout> Layouts => new[]
        {
            ImageLayout.Undefined,
            ImageLayout.General,
            ImageLayout.ShaderReadOnlyOptimal,
            ImageLayout.ColorAttachmentOptimal,
            ImageLayout.DepthStencilAttachmentOptimal,
            ImageLayout.TransferSrcOptimal,
            ImageLayout.TransferDstOptimal,
            ImageLayout.PresentSrcKhr,
        };

        // ---- The masks (V-F6) ----

        /// <summary>
        /// EVERY LAYOUT NAMES A STAGE, with no arm answering <c>NONE</c>. A stage mask of <c>NONE</c> on a side is
        /// half of the incumbent's Release-build behaviour, and a barrier with one has no scope on that side at
        /// all.
        /// </summary>
        [Fact]
        public void EveryLayout_NamesAPipelineStage()
        {
            foreach (ImageLayout layout in Layouts)
            {
                Assert.NotEqual(PipelineStageFlags2.None, VulkanImageTransition.StageFor(layout));
            }
        }

        /// <summary>
        /// AND EVERY LAYOUT THAT CARRIES AN ACCESS NAMES ONE. The two that do not are deliberate and are the whole
        /// list: <c>UNDEFINED</c>, where no access can have happened yet, and <c>PRESENT_SRC_KHR</c>, where the
        /// presentation engine performs no pipeline access and the present semaphore carries the ordering. Pinning
        /// the exception list is what stops a third one arriving quietly.
        /// </summary>
        [Fact]
        public void OnlyUndefinedAndPresent_CarryNoAccessMask()
        {
            foreach (ImageLayout layout in Layouts)
            {
                bool expectedEmpty = layout is ImageLayout.Undefined or ImageLayout.PresentSrcKhr;
                AccessFlags2 access = VulkanImageTransition.AccessFor(layout);

                Assert.Equal(expectedEmpty, access == AccessFlags2.None);
            }
        }

        /// <summary>
        /// NO PAIR OF LAYOUTS PRODUCES AN EMPTY MASK ON BOTH SIDES, over the whole space a barrier can be built
        /// across. What this can catch is narrow, and saying so is the honest version: since each side is answered
        /// from its OWN layout, the only way a pair reaches here empty is a layout that answers <c>NONE</c>, which
        /// <see cref="EveryLayout_NamesAPipelineStage"/> already forecloses one layout at a time. So this loop is
        /// IMPLIED by that one rather than independent of it.
        /// <para>
        /// IT EARNS ITS PLACE AS THE STATEMENT OF WHAT THE PER-LAYOUT MODEL BUYS. Emptiness is impossible here by
        /// construction, and walking the pair space is how that claim is checked to still hold after composition,
        /// including on the day somebody adds a ninth layout or a pair-shaped special case. Under the incumbent's
        /// per-PAIR shape this loop would be the ONLY way to know, and it would be 49 separate facts to keep.
        /// </para>
        /// </summary>
        [Fact]
        public void NoLayoutPair_ProducesAnEmptyMaskOnBothSides()
        {
            foreach (ImageLayout oldLayout in Layouts)
            {
                // BOTH ARMS ARE REFUSED RATHER THAN MERELY SKIPPED, and for two different reasons, each pinned by
                // its own test below. UNDEFINED as an OLD layout discards the image's contents, so it is refused
                // at the general entry point and reachable only through the two named ones (V-F8). UNDEFINED as a
                // NEW layout is invalid outright (VUID-VkImageMemoryBarrier2-newLayout-01198) and is refused in
                // the one constructor every entry point passes through, including the reacquire. So the pair
                // space that a barrier can be built over at all is the seven-by-seven interior, and this loop
                // covers it exactly.
                if (oldLayout == ImageLayout.Undefined) continue;

                foreach (ImageLayout newLayout in Layouts)
                {
                    if (newLayout == ImageLayout.Undefined) continue;

                    ImageMemoryBarrier2 barrier = VulkanImageTransition.For(
                        ImageHandle, Range(), oldLayout, newLayout);

                    Assert.False(
                        barrier.SrcStageMask == PipelineStageFlags2.None
                            && barrier.DstStageMask == PipelineStageFlags2.None,
                        $"{oldLayout} to {newLayout} emitted NONE on both stage masks, which synchronises "
                        + "nothing.");
                }
            }
        }

        /// <summary>
        /// A LAYOUT OUTSIDE THE EIGHT IS REFUSED BY NAME rather than answered with an empty mask. This is the arm
        /// the incumbent got wrong: its unhandled pair fell through to <c>Debug.Fail</c> plus <c>NONE</c>, so in
        /// a Release build the mistake ships as a barrier that orders nothing.
        /// </summary>
        [Fact]
        public void ALayoutOutsideTheEight_ThrowsRatherThanEmittingAnEmptyMask()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => VulkanImageTransition.StageFor(ImageLayout.Preinitialized));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => VulkanImageTransition.AccessFor(ImageLayout.Preinitialized));
        }

        /// <summary>
        /// THE SOURCE SIDE IS THE OLD LAYOUT'S ACCESS AND THE DESTINATION SIDE IS THE NEW LAYOUT'S, which is what
        /// makes the design's table read the way it does: a target sampled after it was rendered into names the
        /// WRITING stage as the source, so the attachment writes are made available to the sampled reads.
        /// </summary>
        [Fact]
        public void ATransition_MakesTheOldLayoutsWritesAvailableToTheNewLayoutsReads()
        {
            ImageMemoryBarrier2 barrier = VulkanImageTransition.For(
                ImageHandle, Range(), ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal);

            Assert.Equal(PipelineStageFlags2.ColorAttachmentOutputBit, barrier.SrcStageMask);
            Assert.True(barrier.SrcAccessMask.HasFlag(AccessFlags2.ColorAttachmentWriteBit));

            Assert.True(barrier.DstStageMask.HasFlag(PipelineStageFlags2.FragmentShaderBit));
            Assert.Equal(AccessFlags2.ShaderSampledReadBit, barrier.DstAccessMask);
        }

        /// <summary>
        /// AND <c>SHADER_READ_ONLY_OPTIMAL</c> NAMES NO WRITE, because the layout permits none. A write access on
        /// a read-only layout describes something the layout forbids, which the synchronisation validation layer
        /// reports and which reads as a correct barrier to everything else.
        /// </summary>
        [Fact]
        public void TheShaderReadOnlyLayout_NamesNoWriteAccess()
        {
            AccessFlags2 access = VulkanImageTransition.AccessFor(ImageLayout.ShaderReadOnlyOptimal);

            Assert.False(access.HasFlag(AccessFlags2.ShaderWriteBit));
            Assert.False(access.HasFlag(AccessFlags2.MemoryWriteBit));
        }

        /// <summary>
        /// EVERY BARRIER IGNORES BOTH QUEUE FAMILIES (V-N5). This backend creates ONE queue on ONE family, so an
        /// ownership transfer is not expressible, and a barrier that named a family would be asking the driver for
        /// a transfer that has no second side.
        /// </summary>
        [Fact]
        public void EveryBarrier_IgnoresBothQueueFamilies()
        {
            ImageMemoryBarrier2 barrier = VulkanImageTransition.For(
                ImageHandle, Range(), ImageLayout.General, ImageLayout.TransferSrcOptimal);

            Assert.Equal(Vk.QueueFamilyIgnored, barrier.SrcQueueFamilyIndex);
            Assert.Equal(Vk.QueueFamilyIgnored, barrier.DstQueueFamilyIndex);
            Assert.Equal(ImageHandle, barrier.Image.Handle);
            Assert.Equal(StructureType.ImageMemoryBarrier2, barrier.SType);
        }

        // ---- The undefined-layout determinism rule (V-F8) ----

        /// <summary>
        /// A TRANSITION OUT OF <c>UNDEFINED</c> IS REFUSED HERE, WHICH IS WHERE THE RULE BECOMES MECHANICAL. It is
        /// the cheap transition, it discards the image's contents, and a discard on contents that are still wanted
        /// produces output that varies by driver and by run. Refusing it at the general entry point means a third
        /// site cannot be written by accident, only by naming one of the two that are allowed.
        /// </summary>
        [Fact]
        public void AnUndefinedOldLayout_IsRefusedAtTheGeneralEntryPoint()
        {
            ArgumentException error = Assert.Throws<ArgumentException>(
                () => VulkanImageTransition.For(
                    ImageHandle, Range(), ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal));

            Assert.Contains("DISCARDS", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// AND <c>UNDEFINED</c> AS A NEW LAYOUT IS REFUSED ON EVERY PATH, WHICH IS A DIFFERENT RULE FROM THE OLD
        /// SIDE'S. It is not a discard and it has no legitimate site at all: VUID-VkImageMemoryBarrier2-newLayout-01198
        /// forbids it because <c>UNDEFINED</c> is the state an image is in before anything has happened to it
        /// rather than one anything can be moved into, and a barrier naming it leaves the image unusable to every
        /// later command in the recording.
        /// <para>
        /// THE REACQUIRE IS ASSERTED HERE TOO, because it is the one entry point that legitimately names
        /// <c>UNDEFINED</c> on the OLD side, which is exactly where a reader would expect the new side to be
        /// permitted as well. The refusal lives in the one constructor both entry points pass through, so it is
        /// not.
        /// </para>
        /// </summary>
        [Fact]
        public void AnUndefinedNewLayout_IsRefusedOnEveryPath()
        {
            ArgumentException general = Assert.Throws<ArgumentException>(
                () => VulkanImageTransition.For(
                    ImageHandle, Range(), ImageLayout.ColorAttachmentOptimal, ImageLayout.Undefined));

            Assert.Contains("newLayout-01198", general.Message, StringComparison.Ordinal);

            Assert.Throws<ArgumentException>(
                () => VulkanImageTransition.Reacquired(ImageHandle, Range(), ImageLayout.Undefined));
        }

        /// <summary>
        /// AND THE ONE SITE IN A RECORDING THAT MAY DISCARD IS THE SWAPCHAIN REACQUIRE, as its own named entry
        /// point. The other legitimate site in the whole backend is a texture's first-ever transition, which is
        /// <c>VulkanSetupBarrier.FirstUse</c> on the device's setup buffer and is tested with that path.
        /// <para>
        /// ITS SOURCE IS THE TOP OF THE PIPE WITH NO ACCESS, because nothing has happened to the image this frame:
        /// a source mask over accesses that cannot have occurred orders nothing and reads as though it did.
        /// </para>
        /// </summary>
        [Fact]
        public void AReacquiredSwapchainImage_IsTheOneRecordingSiteThatMayDiscard()
        {
            ImageMemoryBarrier2 barrier = VulkanImageTransition.Reacquired(
                ImageHandle, Range(), ImageLayout.ColorAttachmentOptimal);

            Assert.Equal(ImageLayout.Undefined, barrier.OldLayout);
            Assert.Equal(ImageLayout.ColorAttachmentOptimal, barrier.NewLayout);
            Assert.Equal(PipelineStageFlags2.TopOfPipeBit, barrier.SrcStageMask);
            Assert.Equal(AccessFlags2.None, barrier.SrcAccessMask);
            Assert.Equal(PipelineStageFlags2.ColorAttachmentOutputBit, barrier.DstStageMask);
        }

        // ---- The present transition (section 10.3's table, last row) ----

        /// <summary>
        /// THE PRESENT TRANSITION IS THE GENERAL BUILDER WITH <c>PRESENT_SRC_KHR</c> AS THE NEW LAYOUT, and its
        /// destination waits for nothing on purpose: the presentation engine is not a pipeline stage, and the
        /// ordering between the frame's writes and the present is carried by the present semaphore.
        /// <para>
        /// THE CALL SITE IS THE SWAPCHAIN'S PRESENT BOUNDARY (row 17,
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/527), which is where the acquired image and its format
        /// are known. What this row owes is the barrier, and what that row owes is emitting it before
        /// <c>vkQueuePresentKHR</c>.
        /// </para>
        /// </summary>
        [Fact]
        public void ThePresentTransition_WaitsForNothingAndNamesNoDestinationAccess()
        {
            ImageMemoryBarrier2 barrier = VulkanImageTransition.For(
                ImageHandle, Range(), ImageLayout.ColorAttachmentOptimal, ImageLayout.PresentSrcKhr);

            Assert.Equal(ImageLayout.PresentSrcKhr, barrier.NewLayout);
            Assert.Equal(PipelineStageFlags2.ColorAttachmentOutputBit, barrier.SrcStageMask);
            Assert.True(barrier.SrcAccessMask.HasFlag(AccessFlags2.ColorAttachmentWriteBit));
            Assert.Equal(PipelineStageFlags2.BottomOfPipeBit, barrier.DstStageMask);
            Assert.Equal(AccessFlags2.None, barrier.DstAccessMask);
        }

        // ---- The subresource range ----

        /// <summary>
        /// AN ATTACHMENT IS MIP 0, LAYER 0, ONE OF EACH, which is not a simplification: the attachment view is
        /// created there at texture creation (V-M11) because <c>CreateFramebuffer</c> carries no mip and no layer
        /// parameter, so a pass cannot render into anything wider than the plane that view names.
        /// </summary>
        [Fact]
        public void TheAttachmentRange_IsMipZeroLayerZero()
        {
            VulkanImageSubrange range = VulkanImageSubrange.Attachment;

            Assert.Equal(0u, range.BaseMipLevel);
            Assert.Equal(1u, range.LevelCount);
            Assert.Equal(0u, range.BaseArrayLayer);
            Assert.Equal(1u, range.LayerCount);
        }

        /// <summary>
        /// AND OVERLAP IS THE RELATION THE TRACKER KEYS ON, so it is pinned here: two ranges of one image overlap
        /// when they share any subresource, and a mip chain's levels are disjoint, which is what makes generating
        /// one a level at a time legal under per-subresource tracking.
        /// </summary>
        [Fact]
        public void TwoRanges_OverlapExactlyWhenTheyShareASubresource()
        {
            var wholeChain = VulkanImageSubrange.Whole(mipLevels: 4, arrayLayers: 1);

            Assert.True(wholeChain.Overlaps(VulkanImageSubrange.Attachment));
            Assert.True(VulkanImageSubrange.Attachment.Overlaps(wholeChain));

            Assert.False(new VulkanImageSubrange(0, 1, 0, 1).Overlaps(new VulkanImageSubrange(1, 1, 0, 1)));
            Assert.False(new VulkanImageSubrange(0, 1, 0, 1).Overlaps(new VulkanImageSubrange(0, 1, 1, 1)));
            Assert.True(new VulkanImageSubrange(0, 2, 0, 1).Overlaps(new VulkanImageSubrange(1, 2, 0, 1)));
        }

        static ImageSubresourceRange Range()
            => new(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
    }
}
