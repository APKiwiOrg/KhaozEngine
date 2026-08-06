using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// SECTION 6.2's SCHEDULE, CLAUSE BY CLAUSE, device-free. Work-breakdown row 11
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/521).
    ///
    /// <para><b>WHAT THIS FILE PINS.</b> That a bind RECORDS ONLY, that the record has two states and not three,
    /// that a flush emits one <c>vkCmdBindDescriptorSets</c> per CONTIGUOUS RUN of dirty slots with
    /// <c>firstSet</c> at the run's start, that a null-set slot is skipped and cuts the run, that repeated marks
    /// between two draws collapse to one flush, and that <c>Begin</c> forgets all of it.</para>
    ///
    /// <para><b>THE COMPOSED OFFSETS AND THE COMPATIBILITY PREFIX HAVE THEIR OWN FILES</b>
    /// (<see cref="VulkanDynamicOffsetTests"/> and <see cref="VulkanLayoutCompatibilityTests"/>), because each is a
    /// different failure class with a different regression story and neither is a corollary of the run
    /// cutting.</para>
    /// </summary>
    public sealed class VulkanBindFlushTests
    {
        /// <summary>
        /// CLAUSE 1: A BIND MAKES NO CALL AT ALL. It stores the set's handle, its layout handle and its dynamic
        /// uniform array, and leaves the slot owing a bind. Ten binds in a row still make zero calls, which is the
        /// whole reason the record exists.
        /// </summary>
        [Fact]
        public void ABind_RecordsAndIssuesNothingUntilTheFlush()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet set = harness.Set("Beam");
            VulkanBoundPipeline pipeline = harness.PipelineFor(set);

            var counts = new VulkanCmdCallCounts();
            var sink = new VulkanCountingCmdSink(counts);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            for (int i = 0; i < 10; i++) records.Record(0, set, 0);

            Assert.Equal(0, counts.BindDescriptorSetCalls);
            Assert.True(records.IsDirty(0));
            Assert.Equal(set.DescriptorSet, records.RecordedSet(0));

            records.Flush(ref sink);

            Assert.Equal(1, counts.BindDescriptorSetCalls);
            Assert.False(records.IsDirty(0));
        }

        /// <summary>
        /// CLAUSE 6, AND THE REASON IT IS NOT BOOKKEEPING. Repeated marks between two flushes collapse to ONE
        /// bind, and the record's size follows the highest SLOT rather than the number of rebinds: a thousand
        /// rebinds of slot zero leave the capacity where one did. Without that the shadow pass, which does
        /// thousands of offsets-only rebinds of one set per frame, is an O(n squared) frame.
        /// </summary>
        [Fact]
        public void AThousandRebindsOfOneSlot_CollapseToOneBindAndGrowNothing()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet set = harness.WindowedSet(slotBytes: 256, slots: 8);
            VulkanBoundPipeline pipeline = harness.PipelineFor(set);

            var counts = new VulkanCmdCallCounts();
            var sink = new VulkanCountingCmdSink(counts);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            int capacity = records.SlotCapacity;
            for (int i = 0; i < 1000; i++) records.Record(0, set, (uint)(i % 8) * 256);

            records.Flush(ref sink);

            Assert.Equal(1, counts.BindDescriptorSetCalls);
            Assert.Equal(1, records.RecordedSlotCount);
            Assert.Equal(capacity, records.SlotCapacity);
        }

        /// <summary>
        /// AND A REBIND THAT CHANGES NOTHING LEAVES THE SLOT CLEAN, which is where the redundancy saving actually
        /// comes from. Re-recording the same set at the same offset after a flush issues nothing at all.
        /// </summary>
        [Fact]
        public void ARedundantRebind_IssuesNothing()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet set = harness.Set("Beam");
            VulkanBoundPipeline pipeline = harness.PipelineFor(set);

            var counts = new VulkanCmdCallCounts();
            var sink = new VulkanCountingCmdSink(counts);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            records.Record(0, set, 0);
            records.Flush(ref sink);
            Assert.Equal(1, counts.BindDescriptorSetCalls);

            records.Record(0, set, 0);
            records.Flush(ref sink);

            Assert.Equal(1, counts.BindDescriptorSetCalls);
        }

        /// <summary>
        /// AND A MARK IS NEVER LOWERED. A record that matches what the slot holds does not clean a slot that was
        /// already owing a bind, because the pending bind has not happened yet. Bind A, bind B, bind A again, then
        /// draw: one bind, carrying A.
        /// </summary>
        [Fact]
        public void ARecordThatMatchesAPendingOne_DoesNotCleanTheSlot()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet first = harness.Set("Beam");
            VulkanResourceSet second = harness.Set("Sky");
            VulkanBoundPipeline pipeline = harness.PipelineFor(first);

            var binds = new List<VulkanRecordedBind>();
            var sink = new VulkanCapturingCmdSink(binds);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            records.Record(0, first, 0);
            records.Record(0, second, 0);
            records.Record(0, first, 0);
            records.Flush(ref sink);

            VulkanRecordedBind bind = Assert.Single(binds);
            Assert.Equal(new[] { first.DescriptorSet }, bind.Sets);
        }

        /// <summary>
        /// CLAUSE 3: A CONTIGUOUS RUN OF DIRTY SLOTS IS ONE CALL, with <c>firstSet</c> at the run's start. Four
        /// sets at slots 0 to 3 activate in ONE <c>vkCmdBindDescriptorSets</c> carrying four sets, which is the
        /// whole Vulkan argument for the descriptor model and the thing a Direct3D 11-shaped budget would miss.
        /// </summary>
        [Fact]
        public void AFullActivation_IsOneCallCarryingEverySet()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet[] sets =
            [
                harness.Set("SpriteBatch.texture"), harness.Set("SpriteBatch.vp"),
                harness.Set("Beam"), harness.Set("Distortion"),
            ];
            VulkanBoundPipeline pipeline = harness.PipelineFor(sets);

            var binds = new List<VulkanRecordedBind>();
            var sink = new VulkanCapturingCmdSink(binds);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            for (uint slot = 0; slot < 4; slot++) records.Record(slot, sets[slot], 0);
            records.Flush(ref sink);

            VulkanRecordedBind bind = Assert.Single(binds);
            Assert.Equal(0u, bind.FirstSet);
            Assert.Equal(sets.Select(s => s.DescriptorSet).ToArray(), bind.Sets);
            Assert.Equal(pipeline.Layout, bind.PipelineLayout);
            Assert.Equal(PipelineBindPoint.Graphics, bind.BindPoint);
        }

        /// <summary>
        /// AND A RUN STARTING PAST ZERO CARRIES ITS OWN <c>firstSet</c>. <c>SpriteBatch</c> puts its uniform buffer
        /// at SET 1, so "set 0 first" is false in shipped code and a flush that assumed it would bind the sprite
        /// batch's view-projection into the texture slot.
        /// </summary>
        [Fact]
        public void ARunStartingPastZero_CarriesItsOwnFirstSet()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet texture = harness.Set("SpriteBatch.texture");
            VulkanResourceSet vp = harness.WindowedSet(slotBytes: 256, slots: 4);
            VulkanBoundPipeline pipeline = harness.PipelineFor(texture, vp);

            var binds = new List<VulkanRecordedBind>();
            var sink = new VulkanCapturingCmdSink(binds);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            records.Record(0, texture, 0);
            records.Record(1, vp, 0);
            records.Flush(ref sink);
            binds.Clear();

            // Only the uniform slot moves, which is the shipped per-draw shape.
            records.Record(1, vp, 0);
            records.Record(1, vp, 256);
            records.Flush(ref sink);

            VulkanRecordedBind bind = Assert.Single(binds);
            Assert.Equal(1u, bind.FirstSet);
            Assert.Equal(new[] { vp.DescriptorSet }, bind.Sets);
        }

        /// <summary>
        /// A CLEAN SLOT IN THE MIDDLE CUTS THE RUN IN TWO, because <c>vkCmdBindDescriptorSets</c> binds a
        /// contiguous array starting at one index and a hole is not expressible. Two calls, each with its own
        /// <c>firstSet</c>, is the correct answer and rebinding the clean slot to keep one call would be a bind
        /// bought for nothing.
        /// </summary>
        [Fact]
        public void ACleanSlotInTheMiddle_CutsTheRunIntoTwoCalls()
        {
            using var harness = new VulkanBindHarness();

            // Four sets over ONE layout content, so any permutation across the four slots still satisfies the
            // pipeline layout and the run cutting is the only thing under test. "Trail", "Overlay" and "DepthLine"
            // are three shipped names for the same single-vertex-uniform shape, which content dedup collapses to
            // one VkDescriptorSetLayout.
            VulkanResourceSet[] sets =
                [harness.Set("Trail"), harness.Set("Overlay"), harness.Set("DepthLine"), harness.Set("Trail")];
            VulkanBoundPipeline pipeline = harness.PipelineFor(sets);
            Assert.Single(pipeline.SetLayouts.Distinct());

            var binds = new List<VulkanRecordedBind>();
            var sink = new VulkanCapturingCmdSink(binds);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            for (uint slot = 0; slot < 4; slot++) records.Record(slot, sets[slot], 0);
            records.Flush(ref sink);
            binds.Clear();

            // Slots 0, 2 and 3 move. Slot 1 does not.
            records.Record(0, sets[3], 0);
            records.Record(2, sets[0], 0);
            records.Record(3, sets[1], 0);
            records.Flush(ref sink);

            Assert.Equal(2, binds.Count);
            Assert.Equal(0u, binds[0].FirstSet);
            Assert.Single(binds[0].Sets);
            Assert.Equal(2u, binds[1].FirstSet);
            Assert.Equal(2, binds[1].Sets.Length);
        }

        /// <summary>
        /// CLAUSE 5: A SLOT WHOSE RECORDED SET HAS GONE NULL IS SKIPPED, and it cuts the run for the same reason a
        /// clean slot does. It is skipped rather than unbound: a descriptor slot no shader reads costs nothing to
        /// leave, and unbinding it would be a native call spent on nobody's behalf. The skip happens ONCE, because
        /// the slot goes clean on the way past.
        /// </summary>
        [Fact]
        public void ANullSlot_IsSkippedOnceAndCutsTheRun()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet first = harness.Set("Beam");
            VulkanResourceSet third = harness.Set("Sky");
            VulkanBoundPipeline pipeline = harness.PipelineFor(first, first, third);

            var binds = new List<VulkanRecordedBind>();
            var sink = new VulkanCapturingCmdSink(binds);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            records.Record(0, first, 0);
            records.Record(1, null, 0);
            records.Record(2, third, 0);
            records.Flush(ref sink);

            Assert.Equal(2, binds.Count);
            Assert.Equal(0u, binds[0].FirstSet);
            Assert.Equal(2u, binds[1].FirstSet);
            Assert.False(records.IsDirty(1));

            // And the skip does not repeat: nothing changed, so the next flush issues nothing at all.
            binds.Clear();
            records.Flush(ref sink);
            Assert.Empty(binds);
        }

        /// <summary>
        /// A SLOT BOUND TO NULL AFTER HOLDING A SET GOES DIRTY AND THEN SKIPS, rather than reading clean because
        /// the record happened to compare equal to nothing. The mark is spent on the skip, which is what stops the
        /// slot being re-examined at every later draw.
        /// </summary>
        [Fact]
        public void ClearingASlotToNull_MarksItThenSkipsIt()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet set = harness.Set("Beam");
            VulkanBoundPipeline pipeline = harness.PipelineFor(set);

            var binds = new List<VulkanRecordedBind>();
            var sink = new VulkanCapturingCmdSink(binds);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            records.Record(0, set, 0);
            records.Flush(ref sink);
            binds.Clear();

            records.Record(0, null, 0);
            Assert.True(records.IsDirty(0));

            records.Flush(ref sink);

            Assert.Empty(binds);
            Assert.False(records.IsDirty(0));
            Assert.Equal(0UL, records.RecordedSet(0));
        }

        /// <summary>
        /// THE TWO ARMS ARE SEPARATE RECORDS WITH SEPARATE FLUSHES. A graphics bind does not feed a dispatch and a
        /// compute bind does not feed a draw, which is the seam's own rule and Vulkan's, and each flush names its
        /// own <see cref="PipelineBindPoint"/>.
        /// </summary>
        [Fact]
        public void TheTwoArms_AreSeparateRecordsWithTheirOwnBindPoints()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet graphics = harness.Set("Beam");
            VulkanResourceSet compute = harness.Set("OceanFft.row");
            VulkanBoundPipeline graphicsPipeline = harness.PipelineFor(graphics);
            VulkanBoundPipeline computePipeline = harness.PipelineFor(compute);

            var binds = new List<VulkanRecordedBind>();
            var sink = new VulkanCapturingCmdSink(binds);
            var graphicsRecords = new VulkanBindRecords(PipelineBindPoint.Graphics);
            var computeRecords = new VulkanBindRecords(PipelineBindPoint.Compute);
            graphicsRecords.SetPipelineLayout(graphicsPipeline.Layout, graphicsPipeline.SetLayouts);
            computeRecords.SetPipelineLayout(computePipeline.Layout, computePipeline.SetLayouts);

            graphicsRecords.Record(0, graphics, 0);
            computeRecords.Record(0, compute, 0);

            graphicsRecords.Flush(ref sink);
            Assert.Equal(PipelineBindPoint.Graphics, Assert.Single(binds).BindPoint);

            binds.Clear();
            computeRecords.Flush(ref sink);
            Assert.Equal(PipelineBindPoint.Compute, Assert.Single(binds).BindPoint);
        }

        /// <summary>
        /// A FLUSH WITH NO PIPELINE BOUND IS REFUSED BY NAME rather than rounded down to nothing.
        /// <c>vkCmdBindDescriptorSets</c> names the pipeline layout the sets are bound against, so there would be
        /// nothing to bind them under, and the caller's real mistake is a draw before a pipeline.
        /// </summary>
        [Fact]
        public void AFlushWithNoPipelineBound_IsRefusedByName()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet set = harness.Set("Beam");

            var counts = new VulkanCmdCallCounts();
            var sink = new VulkanCountingCmdSink(counts);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.Record(0, set, 0);

            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
                () => records.Flush(ref sink));

            Assert.Contains("no pipeline bound", refused.Message, StringComparison.Ordinal);
            Assert.Equal(0, counts.BindDescriptorSetCalls);
        }

        /// <summary>
        /// AND A FLUSH WITH NOTHING DIRTY NEVER ASKS THE QUESTION, which is what makes a draw with no pending bind
        /// free rather than a refusal waiting to happen.
        /// </summary>
        [Fact]
        public void AFlushWithNothingDirty_IssuesNothingAndNeedsNoPipeline()
        {
            var counts = new VulkanCmdCallCounts();
            var sink = new VulkanCountingCmdSink(counts);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);

            records.Flush(ref sink);

            Assert.Equal(0, counts.BindDescriptorSetCalls);
        }

        /// <summary>
        /// AN OFFSET AGAINST A SET WHOSE LAYOUT DECLARES NO DYNAMIC ELEMENT IS REFUSED BY NAME. Nothing would carry
        /// it: the caller's offset attaches to the declared-dynamic element alone (V-D4), so it would be dropped
        /// and the draw would read the buffer's first slot while the caller believed it had indexed into it. A ZERO
        /// offset is accepted against anything, because dropping zero changes nothing and the seam's no-offset
        /// overload is exactly that call.
        /// </summary>
        [Fact]
        public void ANonZeroOffsetAgainstANonDynamicSet_IsRefusedByName()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet set = harness.Set("Beam");
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);

            ArgumentException refused = Assert.Throws<ArgumentException>(() => records.Record(0, set, 256));
            Assert.Contains("V-D4", refused.Message, StringComparison.Ordinal);

            records.Record(0, set, 0);
            Assert.True(records.IsDirty(0));
        }

        /// <summary>
        /// A WILD SET NUMBER IS REFUSED RATHER THAN GROWN INTO. A slot indexes the pipeline layout's set-layout
        /// array, whose length Vulkan itself caps at <c>maxBoundDescriptorSets</c>, so a number in the millions is
        /// a mismatch and growing into it would let one bind allocate its way to an
        /// <see cref="OutOfMemoryException"/>.
        /// </summary>
        [Fact]
        public void AWildSetNumber_IsRefusedRatherThanGrownInto()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet set = harness.Set("Beam");
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => records.Record(VulkanBindRecords.MaxSlot + 1, set, 0));

            records.Record(VulkanBindRecords.MaxSlot, set, 0);
            Assert.True(records.IsDirty(VulkanBindRecords.MaxSlot));
        }

        // ---- Through the shipped seam members ----

        /// <summary>
        /// ALL FOUR SEAM BINDS ARE LIVE AND ALL FOUR RECORD ONLY. This is the same schedule reached the way a
        /// renderer reaches it, which is what stops the tests above from pinning a type nothing calls.
        /// </summary>
        [Fact]
        public void TheSeamsFourBinds_RecordIntoTheRightArm()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet graphics = harness.Set("Beam");
            VulkanResourceSet dynamic = harness.WindowedSet(slotBytes: 256, slots: 4);
            VulkanResourceSet compute = harness.Set("OceanFft.row");

            using VulkanCommandList list = harness.Fixture.CreateList();
            list.Begin();

            list.SetGraphicsResourceSet(0, graphics);
            list.SetGraphicsResourceSet(1, dynamic, 512);
            list.SetComputeResourceSet(0, compute);
            list.SetComputeResourceSet(1, dynamic, 256);

            Assert.Equal(graphics.DescriptorSet, list.GraphicsBinds.RecordedSet(0));
            Assert.Equal(512u, list.GraphicsBinds.RecordedOffset(1));
            Assert.Equal(compute.DescriptorSet, list.ComputeBinds.RecordedSet(0));
            Assert.Equal(256u, list.ComputeBinds.RecordedOffset(1));

            // Neither arm has seen the other's records.
            Assert.NotEqual(list.GraphicsBinds.RecordedSet(0), list.ComputeBinds.RecordedSet(0));
        }

        /// <summary>
        /// A SET FROM ANOTHER BACKEND IS REFUSED BY NAME AT THE BIND, rather than at the flush where the message
        /// would name a slot instead of a call.
        /// </summary>
        [Fact]
        public void ASetFromAnotherBackend_IsRefusedAtTheBind()
        {
            using var harness = new VulkanBindHarness();
            using VulkanCommandList list = harness.Fixture.CreateList();
            list.Begin();

            Assert.Throws<ArgumentException>(() => list.SetGraphicsResourceSet(0, new ForeignResourceSet()));
        }

        /// <summary>
        /// <c>Begin</c> FORGETS EVERY RECORD AND THE PIPELINE LAYOUT WITH THEM, which is section 6.1's reset
        /// landing in the one place a re-begun list cannot be observed without it. A fresh <c>VkCommandBuffer</c>
        /// holds no descriptor set and no pipeline, so a record that survived would let the first flush of the next
        /// recording skip a bind as clean against state living on another buffer.
        /// </summary>
        [Fact]
        public void Begin_ForgetsEveryRecordAndTheLayoutWithThem()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet set = harness.Set("Beam");
            VulkanBoundPipeline pipeline = harness.PipelineFor(set);

            using VulkanCommandList list = harness.Fixture.CreateList();
            list.Begin();
            list.GraphicsBinds.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);
            list.SetGraphicsResourceSet(0, set);
            list.ComputeBinds.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);
            list.SetComputeResourceSet(0, set);
            list.End();

            list.Begin();

            Assert.Equal(0, list.GraphicsBinds.RecordedSlotCount);
            Assert.Equal(0, list.ComputeBinds.RecordedSlotCount);
            Assert.Equal(0UL, list.GraphicsBinds.PipelineLayout);
            Assert.Equal(0UL, list.ComputeBinds.PipelineLayout);
            Assert.False(list.GraphicsBinds.IsDirty(0));
        }

        /// <summary>
        /// AND THE PRE-COMMAND HOOK IS REACHABLE THROUGH THE LIST, which is how row 15
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525) will reach it: call it FIRST in <c>Draw</c>,
        /// <c>DrawIndexed</c> and <c>Dispatch</c>, then issue.
        /// </summary>
        [Fact]
        public void TheListsFlushHook_DrivesTheSameSchedule()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet set = harness.Set("Beam");
            VulkanBoundPipeline pipeline = harness.PipelineFor(set);

            var counts = new VulkanCmdCallCounts();
            var sink = new VulkanCountingCmdSink(counts);

            using VulkanCommandList list = harness.Fixture.CreateList();
            list.Begin();
            list.GraphicsBinds.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);
            list.SetGraphicsResourceSet(0, set);

            list.FlushGraphicsBinds(ref sink);
            list.FlushGraphicsBinds(ref sink);

            Assert.Equal(1, counts.BindDescriptorSetCalls);

            list.ComputeBinds.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);
            list.SetComputeResourceSet(0, set);
            list.FlushComputeBinds(ref sink);

            Assert.Equal(2, counts.BindDescriptorSetCalls);
        }

        /// <summary>A set no backend created, for the refusal above.</summary>
        sealed class ForeignResourceSet : IGpuResourceSet
        {
            public void Dispose()
            {
            }
        }
    }
}
