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

        /// <summary>
        /// AND THE TRACKER'S BARRIERS LAND IN THE BUDGET SEAM'S OWN TALLIES, which is what makes row 15's per-draw
        /// gate capable of failing. The emitter is substitutable, so the same tracker that drives a real
        /// <c>VulkanCmdSink</c> on a device drives <see cref="VulkanCountingCmdSink"/> here, and both barrier
        /// numbers move.
        /// <para>
        /// THIS IS THE ASSERTION THAT STOPS "<c>BarrierCalls == 0</c> BETWEEN TWO DRAWS" FROM BEING VACUOUS. An
        /// emitter that could only ever be the real one would leave those counters at zero whatever the tracker
        /// did, and a budget that cannot fail reads as evidence while being none.
        /// </para>
        /// </summary>
        [Fact]
        public void TheTrackersImageBarriers_AreCountedByTheBudgetSeam()
        {
            var counts = new VulkanCmdCallCounts();
            var tracker = new VulkanLayoutTracker(new VulkanCountingBarrierRecorder(counts));
            var schedule = new VulkanRenderingSchedule(new FakeVulkanRenderApi(), tracker);

            schedule.SetFramebuffer(Buffer, Framebuffer(
                VulkanRestingLayout.ShaderReadOnlyOptimal, VulkanRestingLayout.ShaderReadOnlyOptimal));
            schedule.PrepareDraw(Buffer);

            // ONE call carrying TWO barriers, which is the batching claim, and neither number is reachable if the
            // tracker's emitter is not substitutable.
            Assert.Equal(1, counts.BarrierCalls);
            Assert.Equal(2, counts.BarriersEmitted);

            tracker.RestoreResting(Buffer);

            Assert.Equal(2, counts.BarrierCalls);
            Assert.Equal(4, counts.BarriersEmitted);
            Assert.Equal(new[] { "PipelineBarrier2(2)", "PipelineBarrier2(2)" }, counts.Trace);
        }

        /// <summary>
        /// A BATCH THAT NEVER REACHED THE DRIVER CHANGED NOTHING, which is why the map is committed AFTER the emit
        /// rather than with the barrier. A batch is ONE <c>vkCmdPipelineBarrier2</c>, so either every transition in
        /// it happened or none did, and a map updated first would claim the whole batch after a recorder that
        /// threw. <c>End</c> would then restore from a layout the image is not in, which is a barrier whose OLD
        /// layout is a lie: the validation layer reports it, and without one the driver may honour it by
        /// discarding.
        /// </summary>
        [Fact]
        public void ARecorderThatThrows_LeavesTheMapNamingTheLayoutTheImageIsActuallyIn()
        {
            var recorder = new ThrowingBarrierRecorder();
            var tracker = new VulkanLayoutTracker(recorder);
            VulkanTrackedImage target = Tracked(ColourImage, VulkanRestingLayout.ShaderReadOnlyOptimal);

            Assert.Throws<InvalidOperationException>(
                () => tracker.TransitionTo(Buffer, target, ImageLayout.ColorAttachmentOptimal));

            Assert.Equal(0, tracker.TouchedCount);
            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, tracker.LayoutOf(target));

            // AND THE RESTORE OWES NOTHING EITHER, because nothing moved. A map that had committed the failed
            // transition would emit a barrier here claiming the image was in COLOR_ATTACHMENT_OPTIMAL.
            recorder.Throwing = false;
            tracker.RestoreResting(Buffer);

            Assert.Equal(0, recorder.CallCount);
        }

        /// <summary>
        /// AND THE SAME HOLDS FOR THE RESTORE ITSELF. A recorder that threw restored nothing, so a map emptied
        /// anyway would leave the next reader believing every image is back at rest.
        /// </summary>
        [Fact]
        public void ARestoreThatThrows_KeepsTheTouchedRangesItDidNotRestore()
        {
            var recorder = new ThrowingBarrierRecorder { Throwing = false };
            var tracker = new VulkanLayoutTracker(recorder);

            tracker.TransitionTo(Buffer, Tracked(ColourImage, VulkanRestingLayout.ShaderReadOnlyOptimal),
                ImageLayout.ColorAttachmentOptimal);

            recorder.Throwing = true;
            Assert.Throws<InvalidOperationException>(() => tracker.RestoreResting(Buffer));

            Assert.Equal(1, tracker.TouchedCount);
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
        /// THE STANDARD STREAMING PATH, END TO END, which is where per-level tracking has to meet a whole-chain
        /// bind: a copy seeds mip 0, mip generation walks the chain a level at a time, and then a draw samples the
        /// WHOLE texture, because the seam has no texture-view type and the sampled view is full-chain by
        /// construction (V-M11). Prescribed as ONE list in `KhaozEngine.Gpu`'s own README, so a tracker that
        /// refused the third step would refuse the documented sequence.
        /// <para>
        /// EVERY PIECE IS TRANSITIONED FROM ITS OWN LAYOUT, which is why the chain's disagreement (levels 0 to 2
        /// left in <c>TRANSFER_SRC_OPTIMAL</c>, level 3 in <c>TRANSFER_DST_OPTIMAL</c>) is not an ambiguity: a
        /// single whole-range barrier could not name a true old layout for all four, and four barriers can.
        /// </para>
        /// <para>
        /// AND THEY COLLAPSE INTO ONE ENTRY, which is the part MV5 cares about: the restore at <c>End</c> owes one
        /// barrier rather than four, and the SECOND sampled bind of the same texture owes none at all.
        /// </para>
        /// </summary>
        [Fact]
        public void TheStreamingPath_SamplesTheWholeChainOverTheLevelsItGenerated()
        {
            var recorder = new FakeVulkanBarrierRecorder();
            var tracker = new VulkanLayoutTracker(recorder);

            tracker.TransitionTo(Buffer, Level(0), ImageLayout.TransferDstOptimal);
            for (uint level = 1; level < 4; level++)
            {
                tracker.TransitionTo(Buffer, Level(level - 1), ImageLayout.TransferSrcOptimal);
                tracker.TransitionTo(Buffer, Level(level), ImageLayout.TransferDstOptimal);
            }

            Assert.Equal(4, tracker.TouchedCount);
            int beforeBind = recorder.CallCount;

            tracker.TransitionTo(Buffer, Chain(4), ImageLayout.ShaderReadOnlyOptimal);

            Assert.Equal(beforeBind + 1, recorder.CallCount);
            VulkanRecordedBarrierBatch bind = recorder.Batches[^1];
            Assert.Equal(4, bind.Barriers.Length);

            Assert.Equal(ImageLayout.TransferSrcOptimal, bind.Barriers[0].OldLayout);
            Assert.Equal(ImageLayout.TransferSrcOptimal, bind.Barriers[1].OldLayout);
            Assert.Equal(ImageLayout.TransferSrcOptimal, bind.Barriers[2].OldLayout);
            Assert.Equal(ImageLayout.TransferDstOptimal, bind.Barriers[3].OldLayout);

            for (int i = 0; i < bind.Barriers.Length; i++)
            {
                Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, bind.Barriers[i].NewLayout);
                Assert.Equal((uint)i, bind.Barriers[i].SubresourceRange.BaseMipLevel);
                Assert.Equal(1u, bind.Barriers[i].SubresourceRange.LevelCount);
            }

            Assert.Equal(1, tracker.TouchedCount);

            // A SECOND BIND OF THE SAME CHAIN IS FREE, which is the collapse paying for itself.
            tracker.TransitionTo(Buffer, Chain(4), ImageLayout.ShaderReadOnlyOptimal);
            Assert.Equal(beforeBind + 1, recorder.CallCount);

            // AND End OWES NOTHING, because the collapsed entry is already at the resting layout the sampled bind
            // moved it to. A model that kept four entries would restore four times.
            tracker.RestoreResting(Buffer);

            Assert.Equal(beforeBind + 1, recorder.CallCount);
            Assert.Equal(0, tracker.TouchedCount);
        }

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

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => tracker.LayoutOf(Chain(2)));

            Assert.Contains("no one layout", error.Message, StringComparison.Ordinal);

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

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => apart.TransitionTo(Buffer, Chain(4, VulkanRestingLayout.General),
                    ImageLayout.TransferSrcOptimal));

            Assert.Contains("WIDER", error.Message, StringComparison.Ordinal);
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

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => tracker.TransitionTo(Buffer,
                    Tracked(ColourImage, VulkanRestingLayout.ShaderReadOnlyOptimal,
                        range: new VulkanImageSubrange(BaseMipLevel: 1, LevelCount: 2, BaseArrayLayer: 0,
                            LayerCount: 1)),
                    ImageLayout.TransferSrcOptimal));

            Assert.Contains("OVERLAPPING", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A RANGE CONTAINED IN A WIDER TRACKED ENTRY IS ANSWERED OVER THAT ENTRY, and this is the shape the
        /// ocean's mip chain produces on every recording after its first
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/623). <c>OceanFftProducer.BuildMipChain</c> seeds one
        /// layer at a time and then calls <c>GenerateMipmaps</c>, which names mip 0 over EVERY layer and collapses
        /// the per-layer entries into one. The next round of copies then asks for a single layer of a mip 0 the
        /// tracker holds whole, which is contained rather than partial, and refusing it took nine tests down on
        /// the vulkan-native leg.
        /// <para>
        /// THE BARRIER COVERS THE ENTRY, NOT THE REQUEST. An entry is uniform, so one barrier over all six layers
        /// from the entry's own layout is valid, and it keeps the tracker at ONE entry to restore at <c>End</c>
        /// instead of the four pieces that naming the entry MINUS the request would produce.
        /// </para>
        /// </summary>
        [Fact]
        public void ARangeInsideAWiderTrackedOne_MovesTheWholeEntry()
        {
            var recorder = new FakeVulkanBarrierRecorder();
            var tracker = new VulkanLayoutTracker(recorder);

            // WHAT GENERATE-MIPMAPS LEAVES: mip 0 over every layer, in one entry, in the blit's source layout.
            tracker.TransitionTo(Buffer, MipZeroOfEveryLayer(), ImageLayout.TransferSrcOptimal);

            // AND WHAT THE NEXT FRAME'S FIRST COPY ASKS FOR: one layer of it.
            tracker.TransitionTo(Buffer, Layer(0), ImageLayout.TransferDstOptimal);

            ImageMemoryBarrier2 moved = Assert.Single(recorder.Batches[^1].Barriers);
            Assert.Equal(ImageLayout.TransferSrcOptimal, moved.OldLayout);
            Assert.Equal(ImageLayout.TransferDstOptimal, moved.NewLayout);
            Assert.Equal(0u, moved.SubresourceRange.BaseArrayLayer);
            Assert.Equal(Layers, moved.SubresourceRange.LayerCount);
            Assert.Equal(1u, moved.SubresourceRange.LevelCount);
            Assert.Equal(1, tracker.TouchedCount);

            // EVERY LATER LAYER OF THAT COPY LOOP IS THEN FREE, because the entry it sits in is already there.
            int batches = recorder.Batches.Count;
            tracker.TransitionTo(Buffer, Layer(3), ImageLayout.TransferDstOptimal);
            Assert.Equal(batches, recorder.Batches.Count);
            Assert.False(tracker.WouldTransition(Layer(3), ImageLayout.TransferDstOptimal));

            // AND THE WIDE RANGE STILL MATCHES ITSELF EXACTLY, which is the next GenerateMipmaps coming round
            // again: one barrier for the whole entry, still one entry.
            tracker.TransitionTo(Buffer, MipZeroOfEveryLayer(), ImageLayout.TransferSrcOptimal);

            ImageMemoryBarrier2 back = Assert.Single(recorder.Batches[^1].Barriers);
            Assert.Equal(ImageLayout.TransferDstOptimal, back.OldLayout);
            Assert.Equal(Layers, back.SubresourceRange.LayerCount);
            Assert.Equal(1, tracker.TouchedCount);
        }

        /// <summary>
        /// AND THE LAYOUT OF A CONTAINED RANGE IS THE ENTRY'S LAYOUT, rather than the refusal a range that
        /// CONTAINS narrower tracked ones gets. The two shapes are opposites: pieces of a wider request may
        /// disagree and have no single answer, and a part of one uniform entry is in that entry's layout by
        /// construction.
        /// </summary>
        [Fact]
        public void TheLayoutOfARangeInsideATrackedOne_IsTheEntrysLayout()
        {
            var tracker = new VulkanLayoutTracker(new FakeVulkanBarrierRecorder());

            tracker.TransitionTo(Buffer, MipZeroOfEveryLayer(), ImageLayout.TransferSrcOptimal);

            Assert.Equal(ImageLayout.TransferSrcOptimal, tracker.LayoutOf(Layer(2)));
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
        /// independently and in either submit order. The incumbent tracked the layout ON the texture instead, and
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

        // ONE MIP LEVEL of a texture that rests where a Sampled one does, which is what a copy and each step of a
        // blit chain name.
        static VulkanTrackedImage Level(uint mip,
            VulkanRestingLayout resting = VulkanRestingLayout.ShaderReadOnlyOptimal)
            => Tracked(ColourImage, resting, range: new VulkanImageSubrange(mip, 1, 0, 1));

        // THE WHOLE CHAIN, which is what a sampled bind names: the sampled view is created over every level and
        // every layer at texture creation, so nothing narrower is expressible through the seam.
        static VulkanTrackedImage Chain(uint levels,
            VulkanRestingLayout resting = VulkanRestingLayout.ShaderReadOnlyOptimal)
            => Tracked(ColourImage, resting, range: VulkanImageSubrange.Whole(levels, arrayLayers: 1));

        // THE OCEAN'S CASCADE MAP: three cascades, two layers each, which is what the failing rows carried.
        const uint Layers = 6;

        // MIP 0 OVER EVERY LAYER, which is what GenerateMipmaps names at its first level and what the tracker
        // collapses the seeding copies' per-layer entries into.
        static VulkanTrackedImage MipZeroOfEveryLayer()
            => Tracked(ColourImage, VulkanRestingLayout.ShaderReadOnlyOptimal,
                range: new VulkanImageSubrange(BaseMipLevel: 0, LevelCount: 1, BaseArrayLayer: 0,
                    LayerCount: Layers));

        // ONE LAYER OF MIP 0, which is what each seeding copy names, and what sits INSIDE the range above.
        static VulkanTrackedImage Layer(uint layer)
            => Tracked(ColourImage, VulkanRestingLayout.ShaderReadOnlyOptimal,
                range: new VulkanImageSubrange(BaseMipLevel: 0, LevelCount: 1, BaseArrayLayer: layer,
                    LayerCount: 1));

        // A colour target that is ALSO sampled, which is the post chain: it rests in SHADER_READ_ONLY_OPTIMAL and
        // therefore pays a transition at every pass that renders into it. The depth target rests in its own
        // attachment layout and pays nothing, which is what keeps the counts above readable.
        static VulkanBoundFramebuffer PostChainFramebuffer()
            => Framebuffer(
                VulkanRestingLayout.ShaderReadOnlyOptimal, VulkanRestingLayout.DepthStencilAttachmentOptimal);

        // A RECORDER THAT FAILS THE NATIVE CALL, which is what a lost device or a sealed buffer looks like from
        // above the seam. It counts the calls it DID make, so a test can tell "nothing was emitted" from "the
        // emit was attempted and threw".
        sealed class ThrowingBarrierRecorder : IVulkanBarrierRecorder
        {
            internal bool Throwing { get; set; } = true;

            internal int CallCount { get; private set; }

            public void Emit(ulong commandBuffer, ReadOnlySpan<ImageMemoryBarrier2> barriers)
            {
                if (Throwing) throw new InvalidOperationException("the native call failed");

                CallCount++;
            }
        }

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
