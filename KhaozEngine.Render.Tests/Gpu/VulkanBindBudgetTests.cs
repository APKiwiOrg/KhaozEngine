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
    /// <c>vkCmdSetViewport</c> and one <c>vkCmdSetScissor</c> per framebuffer CHANGE: LANDED, in row 12
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/522) and asserted in
    /// <c>VulkanRenderingScheduleTests.ViewportAndScissor_FollowFramebufferChangesOnly</c>, not here, because
    /// neither call is a member of the sink. <see cref="TheSeam_CannotSeeTheViewportHalfOfTheGate"/> pins that so
    /// the split stays a stated fact rather than an omission, and it still passes unchanged: row 12 gave the
    /// rendering class its OWN device-free seam rather than widening this one. Zero barriers between two draws
    /// that touch no new texture: the BIND half is here (a whole frame's binds emit none), and the meaningful
    /// version landed with row 14's tracker in
    /// <c>VulkanLayoutTrackerTests.TheBarrierCount_IsBoundedByTouchedTexturesAndNotByDraws</c>
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/524).</description></item>
    /// <item><description><b>(b) Marginal per-draw deltas.</b> 5 distinct meshes against 1 and 18 draws against 6:
    /// HERE, for the bind classes AND the draw class, driven through the SHIPPED <c>Draw</c> member since row 15
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525). An offsets-only rebind being exactly ONE
    /// <c>vkCmdBindDescriptorSets</c>: HERE, and it is MV4's headline.</description></item>
    /// <item><description><b>(c) Trace identity for 8 instances of one mesh against 1:</b> HERE, over the bind
    /// trace and over the draw count, which together are the whole of what instancing may not
    /// change.</description></item>
    /// <item><description><b>(d) Upper bounds on the per-pass barrier count:</b> ROW 14's
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/524), asserted over its own tracker in
    /// <c>VulkanLayoutTrackerTests</c>, because a bound asserted here would be a bound over the bind path, which
    /// emits none by construction.</description></item>
    /// </list></para>
    ///
    /// <para><b>MV4 IS FROZEN, AND ROW 15 IS WHERE THAT HAPPENED.</b> The exit criterion was that the first green
    /// run's marginals are recorded and then frozen, and the last half that was owed is the draw-call one: until
    /// <c>vkCmdDraw</c> was emitted by something, <see cref="VulkanCmdCallCounts.DrawCalls"/> read zero BY
    /// CONSTRUCTION rather than as a finding, and a marginal over a number that cannot move is not a gate. Every
    /// frame below is now recorded through a real <see cref="VulkanCommandList"/> whose draw emitter is
    /// <see cref="VulkanCountingDrawEmitter"/> and whose layout tracker's emitter is
    /// <see cref="VulkanCountingBarrierRecorder"/>, so ONE tally covers the binds, the draws and the barriers of a
    /// whole recording. The per-mesh delta, the per-draw delta, the shape of an offsets-only rebind and the
    /// instancing identity are FROZEN from here: moving any of them needs an argument, not an edit.</para>
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
        /// AND A WHOLE FRAME EMITS NO BARRIER AT ALL, which is V-T2's gated invariant in its shipped form: twenty
        /// draws over five meshes in one pass, recorded through the real <c>Draw</c> member with the layout
        /// tracker's own emitter tallying into the same counts, and not one <c>vkCmdPipelineBarrier2</c>.
        /// <para>
        /// THIS USED TO BE THE BIND HALF ALONE and could not fail: the bind path emits no barrier by
        /// construction. Since row 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/525) the frame really
        /// draws, the pre-command walk really asks the tracker to put every bound image where its binding needs
        /// it, and the answer is still zero because every one of them is already there.
        /// </para>
        /// <para>
        /// AND THE FRAME REALLY BINDS IMAGES, which is the half that made the assertion mean something. Its
        /// material set is the shipped <c>Model</c> layout, so each of the five meshes carries FOUR sampled
        /// textures and the twenty draws ask the tracker to place eighty bound images between them. Zero is
        /// therefore a finding about the resting-layout ruling (a plain sampled texture rests in
        /// <c>SHADER_READ_ONLY_OPTIMAL</c>, which is where a sampled bind wants it) rather than a statement about
        /// a frame that bound none. It also pins the pass boundary: a draw that owes no transition does not end
        /// the render pass instance, so the frame is one begin rather than twenty.
        /// </para>
        /// </summary>
        [Fact]
        public void AFramesWorthOfBinds_EmitsNoBarrier()
        {
            VulkanCmdCallCounts counts = DrawFrame(meshes: 5, drawsPerMesh: 4);

            Assert.Equal(0, counts.BarrierCalls);
            Assert.Equal(0, counts.BarriersEmitted);
            Assert.Equal(20, counts.DrawCalls);
        }

        /// <summary>
        /// AND THE FRAME THAT ASSERTION IS TAKEN OVER REALLY BINDS IMAGES, pinned so the swap cannot be undone by
        /// a later edit that quietly puts a uniform-only set back at slot 1. A budget frame whose sets name no
        /// image makes the barrier count zero BY CONSTRUCTION, which is what it was before row 15's review.
        /// </summary>
        [Fact]
        public void TheBudgetFrame_BindsSetsThatReallyCarryImages()
        {
            using var harness = new VulkanBindHarness();

            VulkanResourceSet material = harness.Set("Model");

            Assert.Equal(4, material.Images.Length);
            foreach (VulkanBoundImage image in material.Images)
            {
                Assert.False(image.Storage);
                Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, image.Layout);
            }
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
        /// <para>
        /// ROW 12 HAS LANDED AND THIS STILL PASSES UNCHANGED, which was the decision the pin existed to force. It
        /// needed a device-free line to assert the emitted viewport's negative height on, and it gave the
        /// rendering class its own (<see cref="IVulkanRenderApi"/>) rather than widening this one: that seam
        /// carries the begin, the end, the two dynamic-state setters and the two clears, none of which scale with
        /// draw count, so no marginal is frozen over any of them and this budget still means exactly what it meant
        /// before.
        /// </para>
        /// </summary>
        [Fact]
        public void TheSeam_CannotSeeTheViewportHalfOfTheGate()
        {
            string[] members = typeof(IVkCmdSink).GetMethods().Select(m => m.Name).ToArray();

            Assert.DoesNotContain("SetViewport", members);
            Assert.DoesNotContain("SetScissor", members);
            Assert.DoesNotContain("BeginRendering", members);

            // AND THE SEAM THAT DOES CARRY THEM IS A DIFFERENT ONE, so "not on the budget seam" is a statement
            // about where they went rather than about their absence. A row that ever merged the two would fail
            // both halves of this at once.
            Assert.False(typeof(IVkCmdSink).IsAssignableFrom(typeof(IVulkanRenderApi)));
            Assert.False(typeof(IVulkanRenderApi).IsAssignableFrom(typeof(IVkCmdSink)));

            string[] rendering = typeof(IVulkanRenderApi).GetMethods().Select(m => m.Name).ToArray();
            Assert.Contains("SetViewport", rendering);
            Assert.Contains("SetScissor", rendering);
            Assert.Contains("BeginRendering", rendering);

            // AND ROW 15 MADE THE SAME CHOICE FOR THE GEOMETRY CLASS
            // (https://github.com/APKiwiOrg/KhaozEngine/issues/525). A vertex bind DOES scale with draw count, so
            // it is the one class where widening the budget seam would have looked defensible, and V-T2 names
            // exactly three classes. It got its own line instead, on the seam that also carries the draws, so the
            // frozen marginals below still mean what they meant.
            Assert.DoesNotContain("BindVertexBuffers", members);
            Assert.DoesNotContain("BindIndexBuffer", members);

            string[] draws = typeof(IVulkanDrawEmitter).GetMethods().Select(m => m.Name).ToArray();
            Assert.Contains("BindVertexBuffers", draws);
            Assert.Contains("BindIndexBuffer", draws);
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
        /// bound at slot 1 under the per-draw vertex block at slot 0, which is the shipped skinned model-renderer
        /// shape, and each distinct mesh costs ONE extra bind carrying ONE set: at one draw per mesh the slot 0
        /// offset does not move, so that slot stays clean across the whole pass.
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

            // AND THE DRAW-CALL HALF, which is row 15's and which completes MV4: one mesh drawn once is one
            // vkCmdDraw and each further mesh is exactly one more.
            Assert.Equal(4, five.DrawCalls - one.DrawCalls);

            // Documentation: what the two frames cost today. One mesh is ONE call carrying both sets, and each
            // further mesh is one more call carrying its own set alone.
            Assert.Equal(1, one.BindDescriptorSetCalls);
            Assert.Equal(2, one.DescriptorSetsBound);
            Assert.Equal(1, one.DrawCalls);
            Assert.Equal(5, five.BindDescriptorSetCalls);
            Assert.Equal(6, five.DescriptorSetsBound);
            Assert.Equal(5, five.DrawCalls);
        }

        /// <summary>
        /// EIGHTEEN DRAWS AGAINST SIX MOVES THE TOTAL BY AN EXACT PER-DRAW DELTA. Every draw past the first of a
        /// mesh is an offsets-only rebind of one set, so twelve extra draws are twelve extra binds and not one
        /// more.
        /// <para>
        /// AND THE DRAW-CALL HALF IS HERE SINCE ROW 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/525),
        /// which is the half MV4's freeze was waiting on: twelve extra draws are twelve extra
        /// <c>vkCmdDraw</c> calls and twelve extra binds, one each, flat.
        /// </para>
        /// </summary>
        [Fact]
        public void EighteenDrawsAgainstSix_MoveTheTotalByAnExactPerDrawDelta()
        {
            VulkanCmdCallCounts six = DrawFrame(meshes: 3, drawsPerMesh: 2);
            VulkanCmdCallCounts eighteen = DrawFrame(meshes: 3, drawsPerMesh: 6);

            Assert.Equal(12, eighteen.BindDescriptorSetCalls - six.BindDescriptorSetCalls);
            Assert.Equal(12, eighteen.DescriptorSetsBound - six.DescriptorSetsBound);

            Assert.Equal(12, eighteen.DrawCalls - six.DrawCalls);

            // Documentation: one bind per draw, flat, because every draw past a mesh's first rebinds one set.
            Assert.Equal(6, six.BindDescriptorSetCalls);
            Assert.Equal(18, eighteen.BindDescriptorSetCalls);
            Assert.Equal(6, six.DrawCalls);
            Assert.Equal(18, eighteen.DrawCalls);
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

            // The frame's sets: one windowed vertex-block set at slot 0 and one per-mesh Model set at slot 1, and
            // each declares exactly one uniform buffer, which under V-D4 is one dynamic descriptor whether or not
            // the engine flagged it dynamic. Every bind therefore carries exactly one offset per set it names.
            Assert.Equal(counts.DescriptorSetsBound, counts.DynamicOffsetsPassed);
        }

        // ---- (c) Trace identity ----

        /// <summary>
        /// EIGHT INSTANCES OF ONE MESH PRODUCE THE SAME BIND TRACE AS ONE, call for call and argument for
        /// argument. Instancing changes the instance count of a draw and nothing else, so a backend whose bind
        /// trace moved with it would be doing per-instance work nobody asked for.
        /// <para>
        /// The DRAW half of this identity (eight instances is still ONE <c>vkCmdDraw</c>) landed with row 15
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525) and is asserted alongside it.
        /// </para>
        /// </summary>
        [Fact]
        public void EightInstancesOfOneMesh_ProduceTheSameBindTraceAsOne()
        {
            (List<VulkanRecordedBind> binds, int draws) one = Instanced(instances: 1);
            (List<VulkanRecordedBind> binds, int draws) eight = Instanced(instances: 8);

            Assert.Equal(Describe(one.binds), Describe(eight.binds));
            Assert.Equal(2, one.binds.Count);

            // AND THE DRAW HALF, which is row 15's: eight instances is still ONE vkCmdDraw per draw call, so the
            // whole recording costs the same two either way.
            Assert.Equal(2, one.draws);
            Assert.Equal(one.draws, eight.draws);
        }

        // ---- Fixtures ----

        // ONE FRAME OF THE SHIPPED MODEL-RENDERER SHAPE, THROUGH THE SHIPPED MEMBERS: the skinned pipeline's two
        // layouts in their shipped slot order, a per-draw dynamic vertex block at slot 0 and a per-mesh MATERIAL
        // set at slot 1, so every draw past a mesh's first is an offsets-only rebind of slot 0 and every new mesh
        // is a rebind of slot 1. Recorded into a REAL VulkanCommandList whose draw emitter and whose layout
        // tracker's emitter both tally into one VulkanCmdCallCounts, which is what makes the marginals below total
        // over the draw path rather than over the bind path alone (MV4).
        //
        // THE MATERIAL SET IS A TEXTURE-CARRYING SHIPPED LAYOUT AND THAT IS LOAD-BEARING. It used to be a second
        // windowed uniform set, so the frame bound no image at all, the per-draw transition walk had nothing to
        // walk, and AFramesWorthOfBinds_EmitsNoBarrier passed BY CONSTRUCTION rather than as a finding. "Model"
        // carries four sampled textures, so the walk really asks the tracker to place four images per draw.
        static VulkanCmdCallCounts DrawFrame(int meshes, int drawsPerMesh)
        {
            using var harness = new VulkanBindHarness();

            VulkanResourceSet frame = harness.WindowedSet(slotBytes: 256, slots: 8);
            var materials = new VulkanResourceSet[meshes];
            for (int i = 0; i < meshes; i++) materials[i] = harness.Set("Model");

            VulkanBoundPipeline pipeline = harness.PipelineFor(frame, materials[0]);

            var counts = new VulkanCmdCallCounts();
            using VulkanCommandList list = harness.CountingList(counts);

            list.Begin();
            list.SetFramebuffer(harness.Target());
            list.GraphicsBinds.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            for (int mesh = 0; mesh < meshes; mesh++)
            {
                list.SetGraphicsResourceSet(1, materials[mesh]);

                for (int draw = 0; draw < drawsPerMesh; draw++)
                {
                    list.SetGraphicsResourceSet(0, frame, (uint)draw * 256);
                    list.Draw(3);
                }
            }

            list.End();
            return counts;
        }

        // ONE MESH DRAWN ONCE, with an instance count that the bind schedule must be blind to.
        static (List<VulkanRecordedBind> Binds, int DrawCalls) Instanced(uint instances)
        {
            using var harness = new VulkanBindHarness();

            VulkanResourceSet frame = harness.Set("Beam");
            VulkanResourceSet material = harness.WindowedSet(slotBytes: 256, slots: 8);
            VulkanBoundPipeline pipeline = harness.PipelineFor(frame, material);

            var binds = new List<VulkanRecordedBind>();
            var counts = new VulkanCmdCallCounts();
            using VulkanCommandList list = harness.CountingList(counts, binds);

            list.Begin();
            list.SetFramebuffer(harness.Target());
            list.GraphicsBinds.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            list.SetGraphicsResourceSet(0, frame);
            list.SetGraphicsResourceSet(1, material, 0);

            // THE ONE ARGUMENT INSTANCING MAY MOVE. Everything above and below it is called identically either
            // way, which is what the trace comparison proves.
            list.Draw(3, instances, 0, 0);

            list.SetGraphicsResourceSet(1, material, 256);
            list.Draw(3, instances, 0, 0);

            list.End();
            return (binds, counts.DrawCalls);
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
