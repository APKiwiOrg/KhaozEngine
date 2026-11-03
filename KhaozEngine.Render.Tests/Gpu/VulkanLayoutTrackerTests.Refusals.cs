using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed partial class VulkanLayoutTrackerTests
    {
        /// <summary>
        /// AND THE FINAL SEMANTICS OF LEVELS THAT DISAGREE: the TRANSITION of a range containing them is defined,
        /// because it makes the range uniform, and the QUERY of that range is not, because no single layout is the
        /// answer. Reporting one would have to pick a level and call it the answer, and a caller that skipped a
        /// barrier on that answer is the corruption the whole model exists to make impossible.
        /// </summary>
        [Fact]
        public void AWiderRangeOverLevelsThatDisagree_TransitionsButCannotBeQueried()
        {
            var recorder = new FakeVulkanBarrierRecorder();
            var tracker = new VulkanLayoutTracker(recorder);

            tracker.TransitionTo(Buffer, Level(0), ImageLayout.TransferSrcOptimal);
            tracker.TransitionTo(Buffer, Level(1), ImageLayout.TransferDstOptimal);

            var refusal = Assert.Throws<VulkanLayoutStateException>(() => tracker.LayoutOf(Chain(2)));
            Assert.IsAssignableFrom<InvalidOperationException>(refusal);
            Assert.Equal(VulkanLayoutRefusal.NoSingleLayout, refusal.Reason);
            Assert.Equal(ColourImage, refusal.Image);
            Assert.Equal(2, refusal.CoveredRangeCount);
            Assert.Equal(0u, refusal.BaseMip);
            Assert.Equal(2u, refusal.MipCount);
            Assert.Equal(0u, refusal.BaseLayer);
            Assert.Equal(1u, refusal.LayerCount);

            // The same range transitions through the same classification, so it was refused as covering levels
            // that disagree and not as a partial overlap.
            tracker.TransitionTo(Buffer, Chain(2), ImageLayout.ShaderReadOnlyOptimal);

            Assert.Equal(2, recorder.Batches[^1].Barriers.Length);
            Assert.Equal(1, tracker.TouchedCount);
            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, tracker.LayoutOf(Chain(2)));
        }

        /// <summary>
        /// A WIDER RANGE OVER LEVELS THE RECORDING NEVER TOUCHED IS STILL ANSWERED WHEN THEY NEED NO BARRIER, and
        /// refused when they do. Everything untracked is at REST (V-F7), so a whole-chain bind to the resting
        /// layout leaves the gap needing nothing, and only a target that is NOT the resting layout would need a
        /// barrier over the request MINUS the tracked pieces, which is a range this tracker cannot name without
        /// subtracting rectangles.
        /// <para>
        /// THE REFUSED ARM IS UNREACHABLE ON THE SHIPPED PATHS AND IS WRITTEN DOWN ANYWAY: a whole-chain range
        /// exists only for a texture with a full-chain sampled view, and <c>Sampled</c> wins the resting ladder
        /// outright, so the target of a whole-chain sampled bind IS the resting layout by construction. It takes a
        /// <c>GenerateMipmaps</c> texture that is not also sampled, which rests in <c>GENERAL</c>, to reach it.
        /// </para>
        /// </summary>
        [Fact]
        public void AWiderRangeOverUntouchedLevels_IsAnsweredAtRestAndRefusedAwayFromIt()
        {
            var recorder = new FakeVulkanBarrierRecorder();
            var tracker = new VulkanLayoutTracker(recorder);

            tracker.TransitionTo(Buffer, Level(0), ImageLayout.TransferDstOptimal);

            // THE GAP NEEDS NOTHING: mips 1 to 3 are at rest and the target is the resting layout, so the only
            // barrier is the one piece that moved.
            tracker.TransitionTo(Buffer, Chain(4), ImageLayout.ShaderReadOnlyOptimal);

            ImageMemoryBarrier2 moved = Assert.Single(recorder.Batches[^1].Barriers);
            Assert.Equal(ImageLayout.TransferDstOptimal, moved.OldLayout);
            Assert.Equal(1, tracker.TouchedCount);

            // AND THE GAP THAT WOULD NEED ONE IS REFUSED BY NAME rather than guessed at.
            var apart = new VulkanLayoutTracker(new FakeVulkanBarrierRecorder());
            apart.TransitionTo(Buffer, Level(0, VulkanRestingLayout.General), ImageLayout.TransferDstOptimal);

            VulkanLayoutStateException error = Assert.Throws<VulkanLayoutStateException>(
                () => apart.TransitionTo(Buffer, Chain(4, VulkanRestingLayout.General),
                    ImageLayout.TransferSrcOptimal));

            Assert.Equal(VulkanLayoutRefusal.UntrackedRemainder, error.Reason);
            Assert.Equal(ColourImage, error.Image);
            Assert.Equal(4u, error.MipCount);
            Assert.Equal((int)ImageLayout.TransferSrcOptimal, error.TargetLayout);
            Assert.Equal((int)ImageLayout.General, error.RestingLayout);
        }

        /// <summary>
        /// AND TWO RANGES THAT PARTIALLY OVERLAP ARE REFUSED RATHER THAN MISTRACKED. Transitioning the request
        /// would move PART of the tracked range, so its entry would claim a layout the rest of it no longer has,
        /// and the restore at <c>End</c> would then name an OLD layout the image is not in, which is a barrier the
        /// driver may honour by discarding. Refusing names the mistake where it is made. A range that CONTAINS the
        /// tracked ones is a different case and is answered, which is the test above, and so is a range CONTAINED
        /// IN a tracked one, which is the test below.
        /// <para>
        /// THE PAIR HERE REALLY DOES OVERLAP PARTIALLY, and saying so is the point: mips 0 to 1 and mips 1 to 2
        /// share mip 1 and neither holds the other. This case used to be asserted with a pair where the tracked
        /// range CONTAINED the request, which the refusal caught for the wrong reason and which
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/623 then hit on the shipped ocean path.
        /// </para>
        /// </summary>
        [Fact]
        public void OverlappingRangesOfOneImage_AreRefusedRatherThanMistracked()
        {
            var tracker = new VulkanLayoutTracker(new FakeVulkanBarrierRecorder());

            tracker.TransitionTo(Buffer,
                Tracked(ColourImage, VulkanRestingLayout.ShaderReadOnlyOptimal,
                    range: new VulkanImageSubrange(BaseMipLevel: 0, LevelCount: 2, BaseArrayLayer: 0,
                        LayerCount: 1)),
                ImageLayout.TransferDstOptimal);

            VulkanLayoutStateException error = Assert.Throws<VulkanLayoutStateException>(
                () => tracker.TransitionTo(Buffer,
                    Tracked(ColourImage, VulkanRestingLayout.ShaderReadOnlyOptimal,
                        range: new VulkanImageSubrange(BaseMipLevel: 1, LevelCount: 2, BaseArrayLayer: 0,
                            LayerCount: 1)),
                    ImageLayout.TransferSrcOptimal));

            Assert.Equal(VulkanLayoutRefusal.PartialOverlap, error.Reason);
            Assert.Equal(ColourImage, error.Image);
            Assert.Equal(1u, error.RequestedBaseMip);
            Assert.Equal(2u, error.RequestedMipCount);
            Assert.Equal(0u, error.TrackedBaseMip);
            Assert.Equal(2u, error.TrackedMipCount);
        }

        /// <summary>
        /// A STAGING TEXTURE CANNOT BE TRANSITIONED, because it is a <c>VkBuffer</c> with a software subresource
        /// layout and has no image, no view and no layout at all (V-C7). It carries the null image handle, so the
        /// refusal is by name rather than a barrier against handle 0.
        /// </summary>
        [Fact]
        public void AStagingTexture_HasNoLayoutToTransition()
        {
            var tracker = new VulkanLayoutTracker(new FakeVulkanBarrierRecorder());
            var staging = new VulkanTrackedImage(0, GpuPixelFormat.R8G8B8A8UNorm, DepthStencil: false,
                VulkanRestingLayout.None, VulkanImageSubrange.Attachment);

            var refusal = Assert.Throws<VulkanLayoutArgumentException>(
                () => tracker.TransitionTo(Buffer, staging, ImageLayout.TransferSrcOptimal));
            Assert.IsAssignableFrom<ArgumentException>(refusal);
            Assert.Equal(VulkanLayoutRefusal.ImageHandleMissing, refusal.Reason);
            Assert.Equal("image", refusal.ParamName);
            Assert.Equal(0UL, refusal.Image);
        }
    }
}
