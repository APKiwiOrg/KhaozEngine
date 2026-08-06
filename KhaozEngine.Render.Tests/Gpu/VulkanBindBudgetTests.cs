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
    /// DECISION V-T2's DEVICE-FREE NATIVE-CALL BUDGET, and MEASUREMENT GATE MV4. Work-breakdown row 11
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/521).
    ///
    /// <para><b>THE BET MV4 STATES.</b> The descriptor model collapses a full activation to ONE
    /// <c>vkCmdBindDescriptorSets</c> and an offsets-only rebind to ONE, with zero descriptor writes during
    /// recording. There is no kill switch because it is a call-count property with no runtime risk, and the exit
    /// criterion is that the first green run's marginals are recorded and then frozen.</para>
    ///
    /// <para><b>WHAT IS MEASURED THROUGH IS THE SHIPPED SCHEDULE, not a copy of it.</b>
    /// <see cref="VulkanCountingCmdSink"/> implements the same <see cref="IVkCmdSink"/> the real sink does, and
    /// everything that DECIDES which calls to make (the dirty tracking, the run cutting, the positional offset
    /// composition, the compatibility invalidation) sits above that line and is driven unchanged.</para>
    ///
    /// <para><b>AIMING THIS AT DIRECT3D 11's CALL CLASSES WOULD HAVE BEEN THE MISTAKE.</b> That API binds
    /// RESOURCES and its fan-out defect was one call per resource per stage. Vulkan binds SETS and the resources
    /// went into the set at creation, so the Vulkan fan-out class is per-draw descriptor set ALLOCATION, per-draw
    /// <c>vkUpdateDescriptorSets</c> and per-draw barrier emission. A budget ported from the other backend would
    /// pass green while a Vulkan backend allocated a descriptor set per draw.</para>
    ///
    /// <para><b>WHICH HALVES OF T2's GATE LAND HERE, AND WHICH ARE STILL OWED. This paragraph is the ledger, and
    /// MV4's freeze happens when the last half lands.</b>
    /// <list type="bullet">
    /// <item><description><b>(a) Structural invariants.</b> Zero descriptor allocations and zero descriptor writes
    /// during recording: HERE, against the fake pool, with binds in the middle of the cycle
    /// (<see cref="RecordingAndBindingEveryShippedShape_MakesNoDescriptorCallAtAll"/>). Exactly one
    /// <c>vkCmdSetViewport</c> and one <c>vkCmdSetScissor</c> per framebuffer CHANGE: ROW 12's
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/522), and not countable here at all, because neither is a
    /// member of the sink. <see cref="TheSeam_CannotSeeTheViewportHalfOfTheGate"/> pins that so the split is a
    /// stated fact rather than an omission. Zero barriers between two draws that touch no new texture: the BIND
    /// half is here (a whole frame's binds emit none), and the meaningful version needs row 14's tracker
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/524).</description></item>
    /// <item><description><b>(b) Marginal per-draw deltas.</b> 5 distinct meshes against 1 and 18 draws against 6:
    /// HERE, for the BIND classes, driven through the flush hook exactly as row 15
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525) will drive it from <c>Draw</c>. The DRAW-call half of
    /// the same deltas is row 15's, because <c>vkCmdDraw</c> is not emitted by anything yet. An offsets-only rebind
    /// being exactly ONE <c>vkCmdBindDescriptorSets</c>: HERE, and it is MV4's headline.</description></item>
    /// <item><description><b>(c) Trace identity for 8 instances of one mesh against 1:</b> HERE, over the bind
    /// trace, which is the whole of what instancing may not change.</description></item>
    /// <item><description><b>(d) Upper bounds on the per-pass barrier count:</b> ROW 14's
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/524). Nothing emits a barrier yet, so a bound asserted here
    /// would be a bound over zero.</description></item>
    /// </list></para>
    ///
    /// <para><b>ABSOLUTE TOTALS ARE DOCUMENTATION AND MAY BE UPDATED FREELY.</b> A test that is routinely edited to
    /// match reality stops being a gate, so what is FROZEN is the marginals: the per-mesh and per-draw deltas and
    /// the shape of a rebind. Where an absolute number appears below it is there to say what the frame costs
    /// today, and moving it needs no argument beyond the change that moved it.</para>
    /// </summary>
    public sealed class VulkanBindBudgetTests
    {
        // ---- (a) Structural invariants ----

        /// <summary>
        /// DECISION V-D2's ZERO-COUNT ASSERTION WITH THE BINDS IN THE MIDDLE OF THE CYCLE, which is the shape row
        /// 10 wrote its version to take without moving. Every shipped layout shape becomes a real resource set
        /// BEFORE recording opens, then a whole record-bind-flush-submit cycle moves neither the allocate counter
        /// nor the update counter.
        /// <para>
        /// THE UNREACHABILITY WALK IS THE STRONGER GUARANTEE and this is still worth having: the walk answers a
        /// question about the type graph, and this answers one about the shipped shapes actually being bindable
        /// with nothing left over for a draw to finish.
        /// </para>
        /// </summary>
        [Fact]
        public void RecordingAndBindingEveryShippedShape_MakesNoDescriptorCallAtAll()
        {
            using var harness = new VulkanBindHarness();

            var sets = new List<VulkanResourceSet>();
            foreach (string name in VulkanDescriptorLimitTests.ShippedLayouts.Keys) sets.Add(harness.Set(name));

            int allocatesBefore = harness.Fixture.DescriptorApi.AllocateCount;
            int updatesBefore = harness.Fixture.DescriptorApi.UpdateCount;
            Assert.Equal(VulkanDescriptorLimitTests.ShippedLayouts.Count, allocatesBefore);

            var counts = new VulkanCmdCallCounts();
            var sink = new VulkanCountingCmdSink(counts);

            using VulkanCommandList list = harness.Fixture.CreateList();
            list.Begin();

            foreach (VulkanResourceSet set in sets)
            {
                VulkanBoundPipeline pipeline = harness.PipelineFor(set);
                list.GraphicsBinds.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);
                list.SetGraphicsResourceSet(0, set);
                list.FlushGraphicsBinds(ref sink);
            }

            list.End();
            harness.Fixture.Submits.Submit(list, null);

            Assert.Equal(VulkanDescriptorLimitTests.ShippedLayouts.Count, counts.BindDescriptorSetCalls);
            Assert.Equal(allocatesBefore, harness.Fixture.DescriptorApi.AllocateCount);
            Assert.Equal(updatesBefore, harness.Fixture.DescriptorApi.UpdateCount);
        }

        /// <summary>
        /// AND A WHOLE FRAME'S WORTH OF BINDS EMITS NO BARRIER AT ALL, which is the bind half of "zero barriers
        /// between two draws in one pass that touch no new texture". The meaningful version of that invariant needs
        /// row 14's tracker (https://github.com/APKiwiOrg/KhaozEngine/issues/524), which is what would emit one;
        /// what this pins is that the BIND path never will.
        /// </summary>
        [Fact]
        public void AFramesWorthOfBinds_EmitsNoBarrier()
        {
            VulkanCmdCallCounts counts = DrawFrame(meshes: 5, drawsPerMesh: 4);

            Assert.Equal(0, counts.BarrierCalls);
            Assert.Equal(0, counts.BarriersEmitted);
        }

        /// <summary>
        /// THE VIEWPORT AND SCISSOR HALF OF T2's GATE IS NOT COUNTABLE HERE, AND SAYING SO IS THE POINT. Neither
        /// <c>vkCmdSetViewport</c> nor <c>vkCmdSetScissor</c> is a member of the sink: both go straight to
        /// <c>vkCmd*</c> because neither scales with draw count, and freezing numbers over them would gate on
        /// figures nobody should gate on. "Exactly one of each per framebuffer CHANGE and zero for a redundant
        /// rebind" is therefore row 12's own assertion
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/522) and not a gap in this file.
        /// <para>
        /// Pinned as a test rather than left as a comment, so that adding either member to the sink is a decision
        /// somebody makes here deliberately rather than one that quietly widens what the budget gates on.
        /// </para>
        /// </summary>
        [Fact]
        public void TheSeam_CannotSeeTheViewportHalfOfTheGate()
        {
            string[] members = typeof(IVkCmdSink).GetMethods().Select(m => m.Name).ToArray();

            Assert.DoesNotContain("SetViewport", members);
            Assert.DoesNotContain("SetScissor", members);
            Assert.DoesNotContain("BeginRendering", members);
        }

        // ---- (b) Marginal per-draw deltas ----

        /// <summary>
        /// MV4's HEADLINE: AN OFFSETS-ONLY REBIND IS EXACTLY ONE <c>vkCmdBindDescriptorSets</c>, CARRYING EXACTLY
        /// ONE SET. This is the shadow pass's whole per-draw cost, the thing that made phase 2's shadow encode
        /// collapse on the other backend, and the reason a third dirty state buys nothing here: one call is the
        /// floor whatever changed.
        /// </summary>
        [Fact]
        public void AnOffsetsOnlyRebind_IsExactlyOneBindCarryingOneSet()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet frame = harness.Set("Beam");
            VulkanResourceSet cascades = harness.WindowedSet(slotBytes: 256, slots: 8);
            VulkanBoundPipeline pipeline = harness.PipelineFor(frame, cascades);

            var counts = new VulkanCmdCallCounts();
            var sink = new VulkanCountingCmdSink(counts);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            records.Record(0, frame, 0);
            records.Record(1, cascades, 0);
            records.Flush(ref sink);

            int callsAfterActivation = counts.BindDescriptorSetCalls;
            int setsAfterActivation = counts.DescriptorSetsBound;

            records.Record(1, cascades, 256);
            records.Flush(ref sink);

            Assert.Equal(callsAfterActivation + 1, counts.BindDescriptorSetCalls);
            Assert.Equal(setsAfterActivation + 1, counts.DescriptorSetsBound);

            // AND THE FULL ACTIVATION BEFORE IT WAS ONE CALL CARRYING TWO SETS, which is the other half of the same
            // bet and the number a Direct3D 11-shaped budget cannot express.
            Assert.Equal(1, callsAfterActivation);
            Assert.Equal(2, setsAfterActivation);
        }

        /// <summary>
        /// FIVE DISTINCT MESHES AGAINST ONE MOVES THE TOTAL BY AN EXACT PER-MESH DELTA. A mesh is a material set
        /// bound at slot 1 under a shared frame set at slot 0, which is the shipped model-renderer shape, and each
        /// distinct mesh costs ONE extra bind carrying ONE set: the frame set stays clean across the whole pass.
        /// <para>
        /// FROZEN AS A MARGINAL. The absolute totals below are documentation.
        /// </para>
        /// </summary>
        [Fact]
        public void FiveMeshesAgainstOne_MoveTheTotalByAnExactPerMeshDelta()
        {
            VulkanCmdCallCounts one = DrawFrame(meshes: 1, drawsPerMesh: 1);
            VulkanCmdCallCounts five = DrawFrame(meshes: 5, drawsPerMesh: 1);

            Assert.Equal(4, five.BindDescriptorSetCalls - one.BindDescriptorSetCalls);
            Assert.Equal(4, five.DescriptorSetsBound - one.DescriptorSetsBound);

            // Documentation: what the two frames cost today. One mesh is ONE call carrying both sets, and each
            // further mesh is one more call carrying its own set alone.
            Assert.Equal(1, one.BindDescriptorSetCalls);
            Assert.Equal(2, one.DescriptorSetsBound);
            Assert.Equal(5, five.BindDescriptorSetCalls);
            Assert.Equal(6, five.DescriptorSetsBound);
        }

        /// <summary>
        /// EIGHTEEN DRAWS AGAINST SIX MOVES THE TOTAL BY AN EXACT PER-DRAW DELTA. Every draw past the first of a
        /// mesh is an offsets-only rebind of one set, so twelve extra draws are twelve extra binds and not one
        /// more.
        /// <para>
        /// THE DRAW-CALL HALF OF THIS DELTA IS ROW 15's (https://github.com/APKiwiOrg/KhaozEngine/issues/525):
        /// <c>vkCmdDraw</c> is emitted by nothing yet, so <see cref="VulkanCmdCallCounts.DrawCalls"/> reads zero
        /// here by construction rather than as a finding, and that row completes this assertion by driving the same
        /// frames through the real draw members.
        /// </para>
        /// </summary>
        [Fact]
        public void EighteenDrawsAgainstSix_MoveTheTotalByAnExactPerDrawDelta()
        {
            VulkanCmdCallCounts six = DrawFrame(meshes: 3, drawsPerMesh: 2);
            VulkanCmdCallCounts eighteen = DrawFrame(meshes: 3, drawsPerMesh: 6);

            Assert.Equal(12, eighteen.BindDescriptorSetCalls - six.BindDescriptorSetCalls);
            Assert.Equal(12, eighteen.DescriptorSetsBound - six.DescriptorSetsBound);

            // Row 15 owns these two, and they are zero until it lands.
            Assert.Equal(0, six.DrawCalls);
            Assert.Equal(0, eighteen.DrawCalls);

            // Documentation: one bind per draw, flat, because every draw past a mesh's first rebinds one set.
            Assert.Equal(6, six.BindDescriptorSetCalls);
            Assert.Equal(18, eighteen.BindDescriptorSetCalls);
        }

        /// <summary>
        /// AND THE DYNAMIC-OFFSET COUNT PASSED IS ALWAYS THE SUM OF THAT CALL'S SETS' DYNAMIC DESCRIPTORS, over a
        /// whole frame. This is the invariant that pins the incumbent's own defect: its batching flush resets the
        /// batch count and the first set but NOT the accumulated dynamic-offset count, so a second batch inside one
        /// flush passes a too-large count built from stale entries.
        /// </summary>
        [Fact]
        public void AcrossAWholeFrame_TheOffsetCountIsTheSumOfTheRunsDynamicDescriptors()
        {
            VulkanCmdCallCounts counts = DrawFrame(meshes: 5, drawsPerMesh: 4);

            // The frame's sets: one frame-uniform set (1 dynamic descriptor) plus one per-mesh windowed set (1
            // each). Every bind therefore carries exactly one offset per set it names.
            Assert.Equal(counts.DescriptorSetsBound, counts.DynamicOffsetsPassed);
        }

        // ---- (c) Trace identity ----

        /// <summary>
        /// EIGHT INSTANCES OF ONE MESH PRODUCE THE SAME BIND TRACE AS ONE, call for call and argument for
        /// argument. Instancing changes the instance count of a draw and nothing else, so a backend whose bind
        /// trace moved with it would be doing per-instance work nobody asked for.
        /// <para>
        /// The DRAW half of this identity (eight instances is still ONE <c>vkCmdDraw</c>) is row 15's
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525), for the same reason as above.
        /// </para>
        /// </summary>
        [Fact]
        public void EightInstancesOfOneMesh_ProduceTheSameBindTraceAsOne()
        {
            List<VulkanRecordedBind> one = Instanced(instances: 1);
            List<VulkanRecordedBind> eight = Instanced(instances: 8);

            Assert.Equal(Describe(one), Describe(eight));
            Assert.Equal(2, one.Count);
        }

        // ---- Fixtures ----

        // ONE FRAME OF THE SHIPPED MODEL-RENDERER SHAPE, driven through the flush hook exactly as row 15 will
        // drive it from Draw: a frame-uniform set pinned at slot 0, a per-mesh material set at slot 1, and every
        // draw past a mesh's first being an offsets-only rebind of that second set.
        static VulkanCmdCallCounts DrawFrame(int meshes, int drawsPerMesh)
        {
            using var harness = new VulkanBindHarness();

            VulkanResourceSet frame = harness.Set("Beam");
            var materials = new VulkanResourceSet[meshes];
            for (int i = 0; i < meshes; i++) materials[i] = harness.WindowedSet(slotBytes: 256, slots: 8);

            VulkanBoundPipeline pipeline = harness.PipelineFor(frame, materials[0]);

            var counts = new VulkanCmdCallCounts();
            var sink = new VulkanCountingCmdSink(counts);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            records.Record(0, frame, 0);

            for (int mesh = 0; mesh < meshes; mesh++)
            {
                for (int draw = 0; draw < drawsPerMesh; draw++)
                {
                    records.Record(1, materials[mesh], (uint)draw * 256);
                    records.Flush(ref sink);
                }
            }

            return counts;
        }

        // ONE MESH DRAWN ONCE, with an instance count that the bind schedule must be blind to.
        static List<VulkanRecordedBind> Instanced(uint instances)
        {
            using var harness = new VulkanBindHarness();

            VulkanResourceSet frame = harness.Set("Beam");
            VulkanResourceSet material = harness.WindowedSet(slotBytes: 256, slots: 8);
            VulkanBoundPipeline pipeline = harness.PipelineFor(frame, material);

            var binds = new List<VulkanRecordedBind>();
            var sink = new VulkanCapturingCmdSink(binds);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            records.Record(0, frame, 0);
            records.Record(1, material, 0);
            records.Flush(ref sink);

            // Row 15's Draw(vertexCount, instances, ...) goes here. The bind flush above is everything the
            // instance count could have influenced, and it is called identically either way.
            sink.Draw(3, instances, 0, 0);

            records.Record(1, material, 256);
            records.Flush(ref sink);

            return binds;
        }

        // A bind trace as comparable text: everything a bind carries EXCEPT the descriptor set handles, which
        // differ between two harnesses by construction. What must be identical is the call shape, the set COUNT and
        // every composed offset.
        static string[] Describe(IEnumerable<VulkanRecordedBind> binds)
            => binds
                .Select(b => $"{b.BindPoint} first={b.FirstSet} sets={b.Sets.Length} "
                    + $"offsets=[{string.Join(",", b.DynamicOffsets)}]")
                .ToArray();
    }
}
