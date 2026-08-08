using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE LIST-LOCAL LAYOUT TRACKER, DRIVEN WITH NO DEVICE (V-F6 to V-F8, section 10.3). Work-breakdown row 14
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/524).
    ///
    /// <para><b>EVERYTHING THIS ROW CAN GET WRONG IS ABOVE THE NATIVE LINE, WHICH IS WHY IT IS ALL HERE.</b>
    /// <see cref="VulkanBarrierRecorder"/> builds one <c>VkDependencyInfo</c> and makes one call, and takes no
    /// decision at all: which layout an image is in, whether a barrier is needed, what its masks are, and whether
    /// the restore at <c>End</c> owes anything all live in <see cref="VulkanLayoutTracker"/>.</para>
    ///
    /// <para><b>THE ASSERTION THAT MATTERS MOST DOES NOT THROW WHEN IT FAILS.</b> A missing layout transition
    /// renders correctly on a software rasterizer and corrupts on a tiler, and a barrier restored to the wrong
    /// layout is a validation message on one machine and silence on another. That is the whole reason the design
    /// picked a model whose correctness is checkable without a GPU: a list assumes rest, transitions locally, and
    /// restores, so what a recording did to an image is a value a plain <c>[Fact]</c> can read.</para>
    ///
    /// <para><b>AND THE COUNTS HERE ARE MEASUREMENT GATE MV5.</b> The bet is that the resting-layout model costs a
    /// bounded number of barriers per frame and does not scale with draws.
    /// <see cref="TheBarrierCount_IsBoundedByTouchedTexturesAndNotByDraws"/> is that bet asserted.</para>
    /// </summary>
    public sealed class VulkanLayoutTrackerTests
    {
        const ulong Buffer = 0xC0FFEE;
        const ulong ColourImage = 0x100;
        const ulong DepthImage = 0x200;

        // ---- A list assumes REST, which is the ruling the whole model rests on (V-F7) ----

        /// <summary>
        /// THE FIRST TRANSITION OF A RECORDING STARTS FROM THE RESTING LAYOUT, NOT FROM <c>UNDEFINED</c>. That is
        /// the entire content of V-F7: the resting layout is assigned at texture creation and the device's setup
        /// buffer puts the image there, so a list that has transitioned nothing knows every image's layout without
        /// reading anything shared. Starting from <c>UNDEFINED</c> instead would be the cheap answer and would
        /// discard the texture's contents.
        /// </summary>
        [Fact]
        public void TheFirstTransitionOfARecording_StartsFromTheRestingLayout()
        {
            var recorder = new FakeVulkanBarrierRecorder();
            var tracker = new VulkanLayoutTracker(recorder);
            VulkanTrackedImage sampled = Tracked(ColourImage, VulkanRestingLayout.ShaderReadOnlyOptimal);

            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, tracker.LayoutOf(sampled));

            tracker.TransitionTo(Buffer, sampled, ImageLayout.ColorAttachmentOptimal);

            ImageMemoryBarrier2 barrier = Assert.Single(recorder.Barriers);
            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, barrier.OldLayout);
            Assert.Equal(ImageLayout.ColorAttachmentOptimal, barrier.NewLayout);
            Assert.Equal(Buffer, Assert.Single(recorder.Batches).CommandBuffer);
        }

        /// <summary>
        /// A TRANSITION TO THE LAYOUT AN IMAGE IS ALREADY IN EMITS NOTHING, and this is the clause that keeps the
        /// barrier count per PASS rather than per draw. A second sampled bind of the same texture inside one pass
        /// finds it already in <c>SHADER_READ_ONLY_OPTIMAL</c>, and so does the hundredth.
        /// </summary>
        [Fact]
        public void ATransitionToTheLayoutItIsAlreadyIn_EmitsNothing()
        {
            var recorder = new FakeVulkanBarrierRecorder();
            var tracker = new VulkanLayoutTracker(recorder);
            VulkanTrackedImage target = Tracked(ColourImage, VulkanRestingLayout.ShaderReadOnlyOptimal);

            // Already at rest in it, so nothing is even tracked.
            tracker.TransitionTo(Buffer, target, ImageLayout.ShaderReadOnlyOptimal);
            Assert.Equal(0, recorder.CallCount);
            Assert.Equal(0, tracker.TouchedCount);

            tracker.TransitionTo(Buffer, target, ImageLayout.General);
            tracker.TransitionTo(Buffer, target, ImageLayout.General);
            tracker.TransitionTo(Buffer, target, ImageLayout.General);

            Assert.Equal(1, recorder.CallCount);
            Assert.Equal(1, tracker.TouchedCount);
        }

        // ---- The restore at End (V-F7) ----

        /// <summary>
        /// <c>End</c> PUTS EVERY TOUCHED IMAGE BACK, AS ONE BATCHED BARRIER. That is what makes two lists
        /// composable in any submit order: list 2 assumes rest, and list 1 having ended is what makes that true
        /// whatever order they were submitted in.
        /// </summary>
        [Fact]
        public void RestoreResting_PutsEveryTouchedImageBackAsOneBatchedCall()
        {
            var recorder = new FakeVulkanBarrierRecorder();
            var tracker = new VulkanLayoutTracker(recorder);

            tracker.TransitionTo(Buffer, Tracked(ColourImage, VulkanRestingLayout.ShaderReadOnlyOptimal),
                ImageLayout.ColorAttachmentOptimal);
            tracker.TransitionTo(Buffer, Tracked(DepthImage, VulkanRestingLayout.General),
                ImageLayout.TransferSrcOptimal);

            int beforeRestore = recorder.CallCount;
            tracker.RestoreResting(Buffer);

            Assert.Equal(beforeRestore + 1, recorder.CallCount);
            Assert.Equal(0, tracker.TouchedCount);

            VulkanRecordedBarrierBatch restore = recorder.Batches[^1];
            Assert.Equal(2, restore.Barriers.Length);

            Assert.Equal(ImageLayout.ColorAttachmentOptimal, restore.Barriers[0].OldLayout);
            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, restore.Barriers[0].NewLayout);
            Assert.Equal(ImageLayout.TransferSrcOptimal, restore.Barriers[1].OldLayout);
            Assert.Equal(ImageLayout.General, restore.Barriers[1].NewLayout);
        }

        /// <summary>
        /// AND AN IMAGE THE RECORDING ALREADY PUT BACK OWES NOTHING. A restore emitted for a transition that is
        /// not happening is a barrier bought for nothing, and its old and new layouts would be equal, which is a
        /// barrier the validation layer flags as pointless.
        /// </summary>
        [Fact]
        public void RestoreResting_SkipsAnImageTheRecordingAlreadyPutBack()
        {
            var recorder = new FakeVulkanBarrierRecorder();
            var tracker = new VulkanLayoutTracker(recorder);
            VulkanTrackedImage target = Tracked(ColourImage, VulkanRestingLayout.ShaderReadOnlyOptimal);

            tracker.TransitionTo(Buffer, target, ImageLayout.ColorAttachmentOptimal);
            tracker.TransitionTo(Buffer, target, ImageLayout.ShaderReadOnlyOptimal);

            int beforeRestore = recorder.CallCount;
            tracker.RestoreResting(Buffer);

            Assert.Equal(beforeRestore, recorder.CallCount);
            Assert.Equal(0, tracker.TouchedCount);
        }

        /// <summary>A recording that touched nothing owes no call at all, which is every list that only
        /// updates buffers.</summary>
        [Fact]
        public void RestoreResting_OnAnUntouchedRecordingEmitsNothing()
        {
            var recorder = new FakeVulkanBarrierRecorder();

            new VulkanLayoutTracker(recorder).RestoreResting(Buffer);

            Assert.Equal(0, recorder.CallCount);
        }

        /// <summary>
        /// <c>Reset</c> FORGETS EVERY TRANSITION, because a fresh <c>VkCommandBuffer</c> has recorded none.
        /// Dropping the map is correct rather than lossy: the transitions belonged to a recording that was
        /// discarded, so the images are still at rest, and a retained map would let the next recording skip a
        /// barrier as redundant against a transition that lives on a buffer nobody submitted.
        /// </summary>
        [Fact]
        public void Reset_ForgetsEveryTransitionSoTheNextRecordingAssumesRestAgain()
        {
            var recorder = new FakeVulkanBarrierRecorder();
            var tracker = new VulkanLayoutTracker(recorder);
            VulkanTrackedImage target = Tracked(ColourImage, VulkanRestingLayout.ShaderReadOnlyOptimal);

            tracker.TransitionTo(Buffer, target, ImageLayout.ColorAttachmentOptimal);
            tracker.Reset();

            Assert.Equal(0, tracker.TouchedCount);
            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, tracker.LayoutOf(target));

            tracker.TransitionTo(Buffer, target, ImageLayout.ColorAttachmentOptimal);

            Assert.Equal(2, recorder.CallCount);
            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, recorder.Barriers[1].OldLayout);
        }

        // ---- The attachment transitions at the begin, which row 12 deferred here ----

        /// <summary>
        /// EVERY ATTACHMENT GOES INTO ITS ATTACHMENT LAYOUT BEFORE <c>vkCmdBeginRendering</c>, AND THE ORDER IS THE
        /// ASSERTION. A barrier recorded INSIDE an open render pass instance is a different and much narrower call
        /// than the one section 10.3's table describes, so the two fakes share one trace: two separate call logs
        /// cannot see the difference between "before the begin" and "after it".
        /// </summary>
        [Fact]
        public void TheBeginRendering_TransitionsEveryAttachmentBeforeTheNativeBegin()
        {
            var trace = new List<string>();
            var recorder = new FakeVulkanBarrierRecorder(trace);
            var api = new FakeVulkanRenderApi(trace);
            var schedule = new VulkanRenderingSchedule(api, new VulkanLayoutTracker(recorder));

            // A SAMPLED COLOUR TARGET AND A SAMPLED DEPTH TARGET, which is the post chain plus a shadow map: both
            // rest in SHADER_READ_ONLY_OPTIMAL, so both owe a transition and the depth arm's own target layout is
            // pinned rather than inferred from the colour one.
            schedule.SetFramebuffer(Buffer, Framebuffer(
                VulkanRestingLayout.ShaderReadOnlyOptimal, VulkanRestingLayout.ShaderReadOnlyOptimal));
            schedule.PrepareDraw(Buffer);

            Assert.StartsWith("PipelineBarrier2(", trace[0], StringComparison.Ordinal);
            Assert.StartsWith("BeginRendering(", trace[1], StringComparison.Ordinal);

            VulkanRecordedBarrierBatch batch = Assert.Single(recorder.Batches);
            Assert.Equal(2, batch.Barriers.Length);

            Assert.Equal(ColourImage, batch.Barriers[0].Image.Handle);
            Assert.Equal(ImageLayout.ColorAttachmentOptimal, batch.Barriers[0].NewLayout);
            Assert.Equal(DepthImage, batch.Barriers[1].Image.Handle);
            Assert.Equal(ImageLayout.DepthStencilAttachmentOptimal, batch.Barriers[1].NewLayout);
        }

        /// <summary>
        /// AND AN ATTACHMENT ALREADY RESTING IN ITS ATTACHMENT LAYOUT COSTS NOTHING AT EITHER END, which is the
        /// common case and the reason the resting-layout ladder puts a plain render target in
        /// <c>COLOR_ATTACHMENT_OPTIMAL</c>. A model that transitioned unconditionally would pay two barriers per
        /// pass for every ordinary target in the frame.
        /// </summary>
        [Fact]
        public void APlainRenderTarget_PaysNoBarrierAtEitherEnd()
        {
            var recorder = new FakeVulkanBarrierRecorder();
            var tracker = new VulkanLayoutTracker(recorder);
            var schedule = new VulkanRenderingSchedule(new FakeVulkanRenderApi(), tracker);

            schedule.SetFramebuffer(Buffer, Framebuffer(
                VulkanRestingLayout.ColorAttachmentOptimal, VulkanRestingLayout.DepthStencilAttachmentOptimal));
            schedule.PrepareDraw(Buffer);
            tracker.RestoreResting(Buffer);

            Assert.Equal(0, recorder.CallCount);
            Assert.Equal(0, tracker.TouchedCount);
        }

        /// <summary>
        /// THE POST-CHAIN SHAPE PAYS ONE BARRIER IN AND ONE BACK, and paying there is the point. A texture that is
        /// both a render target and <c>Sampled</c> rests in <c>SHADER_READ_ONLY_OPTIMAL</c> so the pass that reads
        /// it needs no barrier at all, and the pass that renders into it transitions and restores.
        /// </summary>
        [Fact]
        public void APostChainTarget_PaysOneBarrierInAndOneBack()
        {
            var recorder = new FakeVulkanBarrierRecorder();
            var tracker = new VulkanLayoutTracker(recorder);
            var schedule = new VulkanRenderingSchedule(new FakeVulkanRenderApi(), tracker);

            schedule.SetFramebuffer(Buffer, Framebuffer(
                VulkanRestingLayout.ShaderReadOnlyOptimal, VulkanRestingLayout.DepthStencilAttachmentOptimal));
            schedule.PrepareDraw(Buffer);

            ImageMemoryBarrier2 into = Assert.Single(recorder.Barriers);
            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, into.OldLayout);
            Assert.Equal(ImageLayout.ColorAttachmentOptimal, into.NewLayout);

            tracker.RestoreResting(Buffer);

            ImageMemoryBarrier2 back = recorder.Batches[^1].Barriers[0];
            Assert.Equal(ImageLayout.ColorAttachmentOptimal, back.OldLayout);
            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, back.NewLayout);
            Assert.Equal(2, recorder.BarrierCount);
        }

        /// <summary>
        /// MEASUREMENT GATE MV5, ASSERTED: the barrier count is proportional to passes times TOUCHED TEXTURES and
        /// does not move with draw count. Twenty draws into one pass emit the pass's transitions once, because a
        /// transition to the layout an image is already in emits nothing.
        /// <para>
        /// A KILL SWITCH IS NOT AVAILABLE HERE AND THAT IS DELIBERATE (MV5): a wrong barrier model is a
        /// correctness failure caught by goldens and by the validation layer, not a tuning knob, so this bet is
        /// checked rather than hedged.
        /// </para>
        /// </summary>
        [Fact]
        public void TheBarrierCount_IsBoundedByTouchedTexturesAndNotByDraws()
        {
            var recorder = new FakeVulkanBarrierRecorder();
            var tracker = new VulkanLayoutTracker(recorder);
            var schedule = new VulkanRenderingSchedule(new FakeVulkanRenderApi(), tracker);

            schedule.SetFramebuffer(Buffer, PostChainFramebuffer());
            for (int i = 0; i < 20; i++) schedule.PrepareDraw(Buffer);

            schedule.EndRendering(Buffer);
            tracker.RestoreResting(Buffer);

            // ONE call in and one back, each carrying ONE barrier: the depth target rests in its own attachment
            // layout, so only the sampled colour target is ever touched.
            Assert.Equal(2, recorder.CallCount);
            Assert.Equal(2, recorder.BarrierCount);
        }

        // ---- Per-subresource tracking (V-F6) ----

        /// <summary>
        /// DISJOINT RANGES OF ONE IMAGE ARE TRACKED SEPARATELY, which is what "per subresource range" buys: a mip
        /// chain is generated one level at a time, with level N-1 in <c>TRANSFER_SRC_OPTIMAL</c> while level N is
        /// in <c>TRANSFER_DST_OPTIMAL</c>, and one layout per image could not express that at all.
        /// </summary>
        [Fact]
        public void DisjointRangesOfOneImage_AreTrackedSeparately()
        {
            var recorder = new FakeVulkanBarrierRecorder();
            var tracker = new VulkanLayoutTracker(recorder);

            VulkanTrackedImage source = Tracked(ColourImage, VulkanRestingLayout.ShaderReadOnlyOptimal,
                range: new VulkanImageSubrange(0, 1, 0, 1));
            VulkanTrackedImage destination = Tracked(ColourImage, VulkanRestingLayout.ShaderReadOnlyOptimal,
                range: new VulkanImageSubrange(1, 1, 0, 1));

            tracker.TransitionTo(Buffer, source, ImageLayout.TransferSrcOptimal);
            tracker.TransitionTo(Buffer, destination, ImageLayout.TransferDstOptimal);

            Assert.Equal(2, tracker.TouchedCount);
            Assert.Equal(ImageLayout.TransferSrcOptimal, tracker.LayoutOf(source));
            Assert.Equal(ImageLayout.TransferDstOptimal, tracker.LayoutOf(destination));

            tracker.RestoreResting(Buffer);
            Assert.Equal(2, recorder.Batches[^1].Barriers.Length);
        }

        /// <summary>
        /// AND TWO RANGES THAT OVERLAP WITHOUT BEING EQUAL ARE REFUSED RATHER THAN MISTRACKED. Two entries sharing
        /// a subresource would disagree about its layout the moment either moved, and the restore at <c>End</c>
        /// would then name an OLD layout the image is not in, which is a barrier the driver may honour by
        /// discarding. Refusing names the mistake where it is made.
        /// </summary>
        [Fact]
        public void OverlappingRangesOfOneImage_AreRefusedRatherThanMistracked()
        {
            var tracker = new VulkanLayoutTracker(new FakeVulkanBarrierRecorder());

            tracker.TransitionTo(Buffer,
                Tracked(ColourImage, VulkanRestingLayout.ShaderReadOnlyOptimal,
                    range: VulkanImageSubrange.Whole(mipLevels: 4, arrayLayers: 1)),
                ImageLayout.TransferDstOptimal);

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => tracker.TransitionTo(Buffer,
                    Tracked(ColourImage, VulkanRestingLayout.ShaderReadOnlyOptimal,
                        range: VulkanImageSubrange.Attachment),
                    ImageLayout.TransferSrcOptimal));

            Assert.Contains("OVERLAPPING", error.Message, StringComparison.Ordinal);
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

            Assert.Throws<ArgumentException>(
                () => tracker.TransitionTo(Buffer, staging, ImageLayout.TransferSrcOptimal));
        }

        // ---- The list wiring (section 6.1) ----

        /// <summary>
        /// THROUGH THE REAL LIST: <c>End</c> RESTORES AND <c>Begin</c> FORGETS. The restore has to land after the
        /// render pass instance closed and before <c>vkEndCommandBuffer</c>, because a barrier recorded after the
        /// native end is a call against a sealed buffer, and the reset has to land in <c>Begin</c>, because a
        /// re-begun list that kept its map would skip a barrier against a record nobody submitted.
        /// </summary>
        [Fact]
        public void TheListRestoresAtEndAndForgetsAtBegin()
        {
            using var fixture = new VulkanCommandListTests.Fixture(depth: 3);
            var recorder = new FakeVulkanBarrierRecorder();
            var tracker = new VulkanLayoutTracker(recorder);

            using var list = new VulkanCommandList(
                new VulkanCommandPoolRing(fixture.Api, 3, fixture.Timeline, fixture.Backpressure),
                fixture.Retired, uploads: null, assertBoundSetLayouts: false,
                render: new FakeVulkanRenderApi(), pipelines: null, layouts: tracker);

            list.Begin();
            list.Rendering.SetFramebuffer(list.Ring.BufferAt(list.Ring.Slot), PostChainFramebuffer());
            list.PrepareDraw();

            Assert.Equal(1, tracker.TouchedCount);

            list.End();

            Assert.Equal(0, tracker.TouchedCount);
            Assert.Equal(2, recorder.CallCount);
            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, recorder.Batches[^1].Barriers[0].NewLayout);

            // AND THE NEXT RECORDING ASSUMES REST AGAIN, which is only observable because End emptied the map and
            // Begin would have emptied it anyway.
            list.Begin();
            Assert.Equal(0, tracker.TouchedCount);
        }

        /// <summary>
        /// TWO LISTS TOUCHING ONE TEXTURE CANNOT DISAGREE, WHICH IS THE WHOLE RULING (V-F7, section 2.5). Nothing
        /// shared is read or written during recording, so each list assumes rest, transitions, and restores,
        /// independently and in either submit order. The incumbent tracks the layout ON the texture instead, and
        /// the loser of that race records either a redundant barrier or NO BARRIER FOR A TRANSITION IT NEEDED,
        /// which is a corruption no golden on a software rasterizer will show.
        /// <para>
        /// AND WHAT IS LOST UNDER THIS RULING, RECORDED SO IT IS NOT MISREAD: <c>OpenListTrackingGpuDevice</c>
        /// passes TRIVIALLY on this leg, exactly as it does on the native Direct3D 11 leg. It stays the PORTABLE
        /// guard and is not evidence about this backend.
        /// </para>
        /// </summary>
        [Fact]
        public void TwoListsTouchingOneTexture_RecordTheSameTransitionsIndependently()
        {
            var first = new FakeVulkanBarrierRecorder();
            var second = new FakeVulkanBarrierRecorder();
            var one = new VulkanLayoutTracker(first);
            var two = new VulkanLayoutTracker(second);
            VulkanTrackedImage shared = Tracked(ColourImage, VulkanRestingLayout.ShaderReadOnlyOptimal);

            one.TransitionTo(Buffer, shared, ImageLayout.ColorAttachmentOptimal);
            two.TransitionTo(Buffer, shared, ImageLayout.ColorAttachmentOptimal);
            one.RestoreResting(Buffer);
            two.RestoreResting(Buffer);

            Assert.Equal(2, first.BarrierCount);
            Assert.Equal(2, second.BarrierCount);

            for (int i = 0; i < 2; i++)
            {
                Assert.Equal(first.Barriers[i].OldLayout, second.Barriers[i].OldLayout);
                Assert.Equal(first.Barriers[i].NewLayout, second.Barriers[i].NewLayout);
            }
        }

        // ---- Fixtures ----

        static VulkanTrackedImage Tracked(ulong image, VulkanRestingLayout resting, bool depthStencil = false,
            VulkanImageSubrange? range = null)
            => new(image, depthStencil ? GpuPixelFormat.D32FloatS8UInt : GpuPixelFormat.R8G8B8A8UNorm,
                depthStencil, resting, range ?? VulkanImageSubrange.Attachment);

        // A colour target that is ALSO sampled, which is the post chain: it rests in SHADER_READ_ONLY_OPTIMAL and
        // therefore pays a transition at every pass that renders into it. The depth target rests in its own
        // attachment layout and pays nothing, which is what keeps the counts above readable.
        static VulkanBoundFramebuffer PostChainFramebuffer()
            => Framebuffer(
                VulkanRestingLayout.ShaderReadOnlyOptimal, VulkanRestingLayout.DepthStencilAttachmentOptimal);

        static VulkanBoundFramebuffer Framebuffer(VulkanRestingLayout colour, VulkanRestingLayout depth)
            => new(
                Id: 1, Width: 64, Height: 64,
                new[]
                {
                    new VulkanAttachment(ColourImage + 1, ColourImage, GpuPixelFormat.R8G8B8A8UNorm,
                        DepthStencil: false, colour),
                },
                new VulkanAttachment(DepthImage + 1, DepthImage, GpuPixelFormat.D32FloatS8UInt,
                    DepthStencil: true, depth));
    }
}
