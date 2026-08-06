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
    /// DECISION V-R7's GUARD, which is the whole reason V-R6's mechanism is safe to have. Work-breakdown row 11
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/521).
    ///
    /// <para><b>THE ASYMMETRY THIS FILE EXISTS FOR.</b> A pipeline switch invalidates bound descriptor sets from
    /// the first INCOMPATIBLE set onward, and the backend computes that boundary as the longest common prefix of
    /// the two pipeline layouts' set-layout HANDLE sequences. A prefix computed SHORTER than the truth costs a
    /// redundant <c>vkCmdBindDescriptorSets</c>. A prefix computed LONGER than the truth leaves a set the driver
    /// has already invalidated marked clean, so the next draw reads whatever that descriptor slot now holds, which
    /// renders wrong and throws nothing. One draft of the design deferred the mechanism to avoid that cliff and the
    /// other took the mechanism and left the cliff unguarded, and this is the check neither had.</para>
    ///
    /// <para><b>THE REFERENCE IS COMPUTED FROM CONTENT, NOT FROM HANDLES, which is what stops the guard being a
    /// tautology.</b> "Identically defined set layouts for sets 0 through N" is a statement about what
    /// <c>vkCreateDescriptorSetLayout</c> reads: the ordered binding table. The handle identity the backend
    /// compares is V-D5's CLAIM about that content. So the reference walks the shipped layout DESCRIPTIONS through
    /// <c>VulkanDescriptorPolicy.BindingsFor</c> and compares binding tables, and the assertion is that the
    /// handle-based answer never exceeds it.</para>
    /// </summary>
    public sealed class VulkanLayoutCompatibilityTests
    {
        /// <summary>
        /// THE GUARD ITSELF, over every ORDERED PAIR of the thirty-three shipped pipelines: 1089 pairs, and for
        /// every one of them the computed compatible prefix is no longer than the true prefix of identically
        /// defined set layouts. The invalidation can therefore only ever be CONSERVATIVE.
        /// <para>
        /// ORDERED rather than unordered, because a switch has a direction and the two layouts have different
        /// lengths in general. Self-pairs are included: switching to the pipeline already current must answer the
        /// full length, which is the identity guard's own case.
        /// </para>
        /// </summary>
        [Fact]
        public void EveryOrderedPairOfShippedPipelines_ComputesNoMoreThanTheTruePrefix()
        {
            using var harness = new VulkanBindHarness();
            IReadOnlyList<(string Name, string[] Slots, VulkanBoundPipeline Bound)> pipelines = Build(harness);

            Assert.Equal(33, pipelines.Count);

            int pairs = 0;
            var seen = new HashSet<int>();

            foreach ((string outName, string[] outSlots, VulkanBoundPipeline outgoing) in pipelines)
            {
                foreach ((string inName, string[] inSlots, VulkanBoundPipeline incoming) in pipelines)
                {
                    int computed = VulkanLayoutCompatibility.CompatiblePrefix(
                        outgoing.SetLayouts, incoming.SetLayouts);
                    int truth = TruePrefix(outSlots, inSlots);

                    Assert.True(computed <= truth,
                        $"Switching from '{outName}' to '{inName}' computes a compatible prefix of {computed} "
                        + $"where only {truth} sets are identically defined. A prefix LONGER than the truth leaves "
                        + "a set the driver invalidated marked clean, so the next draw renders against whatever "
                        + "that descriptor slot now holds. Decision V-R7 requires this computation to be "
                        + "conservative in exactly this direction.");

                    seen.Add(truth);
                    pairs++;
                }
            }

            Assert.Equal(33 * 33, pairs);

            // AND THE GUARD IS NOT VACUOUS. A walk over pairs that were all incompatible would pass an
            // always-answer-zero implementation, so the shipped table has to actually contain compatible pairs.
            Assert.Contains(0, seen);
            Assert.Contains(1, seen);
            Assert.Contains(2, seen);
        }

        /// <summary>
        /// AND THE HANDLE COMPARE IS EXACT ON THE SHIPPED TABLE, which is what content dedup BUYS rather than what
        /// V-R7 requires. The guard above only demands conservatism, and an implementation that always answered
        /// zero would satisfy it while reproducing the incumbent's every-switch-rebinds-everything cost. This is
        /// the assertion that the dedup earns its keep.
        /// </summary>
        [Fact]
        public void OnTheShippedTable_TheHandleCompareIsExactlyTheContentCompare()
        {
            using var harness = new VulkanBindHarness();
            IReadOnlyList<(string Name, string[] Slots, VulkanBoundPipeline Bound)> pipelines = Build(harness);

            foreach ((_, string[] outSlots, VulkanBoundPipeline outgoing) in pipelines)
            {
                foreach ((_, string[] inSlots, VulkanBoundPipeline incoming) in pipelines)
                {
                    Assert.Equal(TruePrefix(outSlots, inSlots),
                        VulkanLayoutCompatibility.CompatiblePrefix(outgoing.SetLayouts, incoming.SetLayouts));
                }
            }
        }

        /// <summary>
        /// THREE SHIPPED NAMES FOR ONE SHAPE REALLY DO SHARE ONE HANDLE, which is the concrete thing the exactness
        /// above rests on. <c>DepthLineRenderer</c>, <c>OverlayRenderer</c> and <c>TrailRenderer</c> each declare a
        /// single vertex-stage uniform buffer, so their pipelines are mutually compatible for their whole array
        /// and switching between them invalidates nothing at all. Without dedup all three would be distinct
        /// objects and every switch would rebind.
        /// </summary>
        [Fact]
        public void ThreeShippedRenderersDeclaringOneShape_AreMutuallyCompatible()
        {
            using var harness = new VulkanBindHarness();

            VulkanBoundPipeline depthLine = harness.Pipeline("DepthLine");
            VulkanBoundPipeline overlay = harness.Pipeline("Overlay");
            VulkanBoundPipeline trail = harness.Pipeline("Trail");

            Assert.Equal(depthLine.Layout, overlay.Layout);
            Assert.Equal(depthLine.Layout, trail.Layout);
            Assert.Equal(1, VulkanLayoutCompatibility.CompatiblePrefix(depthLine.SetLayouts, trail.SetLayouts));
        }

        /// <summary>
        /// AND A PIPELINE THAT SHARES SET 0 AND DIFFERS AT SET 1 KEEPS SET 0 BOUND. No shipped pipeline pair has
        /// that shape (only two families use more than one layout, and both of those pairs are either identical or
        /// disjoint at set 0), so it is built here on purpose: the middle of the prefix rule is the part a
        /// shipped-only sweep cannot reach.
        /// </summary>
        [Fact]
        public void APipelineSharingSetZeroAndDifferingAtSetOne_KeepsSetZero()
        {
            using var harness = new VulkanBindHarness();

            VulkanBoundPipeline first = harness.Pipeline("Beam", "Sky");
            VulkanBoundPipeline second = harness.Pipeline("Beam", "Water");

            Assert.NotEqual(first.Layout, second.Layout);
            Assert.Equal(1, VulkanLayoutCompatibility.CompatiblePrefix(first.SetLayouts, second.SetLayouts));
            Assert.Equal(1, VulkanLayoutCompatibility.CompatiblePrefix(second.SetLayouts, first.SetLayouts));
        }

        /// <summary>
        /// A NULL HANDLE NEVER ESTABLISHES COMPATIBILITY, even against another null handle. Two zeroes are two
        /// absences rather than two identically defined set layouts, and the only failure mode this computation has
        /// is claiming a compatibility that is not there.
        /// </summary>
        [Fact]
        public void ANullHandle_NeverEstablishesCompatibility()
        {
            Assert.Equal(0, VulkanLayoutCompatibility.CompatiblePrefix([0, 7], [0, 7]));
            Assert.Equal(1, VulkanLayoutCompatibility.CompatiblePrefix([7, 0], [7, 0]));
            Assert.Equal(0, VulkanLayoutCompatibility.CompatiblePrefix([], [7]));
            Assert.Equal(2, VulkanLayoutCompatibility.CompatiblePrefix([7, 9, 11], [7, 9]));
        }

        // ---- What the prefix does to the records ----

        /// <summary>
        /// CLAUSE 4: A SWITCH INVALIDATES FROM THE FIRST INCOMPATIBLE SET ONWARD AND LEAVES THE PREFIX ALONE. Set 0
        /// stays clean and set 1 goes dirty, so the next draw rebinds one set rather than two, which is the whole
        /// saving V-D5 exists to make available.
        /// </summary>
        [Fact]
        public void ASwitch_InvalidatesFromTheFirstIncompatibleSetOnward()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet shared = harness.Set("Beam");
            VulkanResourceSet first = harness.Set("Sky");
            VulkanResourceSet second = harness.Set("Water");

            VulkanBoundPipeline before = harness.PipelineFor(shared, first);
            VulkanBoundPipeline after = harness.PipelineFor(shared, second);

            var binds = new List<VulkanRecordedBind>();
            var sink = new VulkanCapturingCmdSink(binds);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);

            records.SetPipelineLayout(before.Layout, before.SetLayouts);
            records.Record(0, shared, 0);
            records.Record(1, first, 0);
            records.Flush(ref sink);
            binds.Clear();

            Assert.Equal(1, records.SetPipelineLayout(after.Layout, after.SetLayouts));
            Assert.False(records.IsDirty(0));
            Assert.True(records.IsDirty(1));

            records.Record(1, second, 0);
            records.Flush(ref sink);

            VulkanRecordedBind bind = Assert.Single(binds);
            Assert.Equal(1u, bind.FirstSet);
            Assert.Equal(new[] { second.DescriptorSet }, bind.Sets);
        }

        /// <summary>
        /// AN INCOMPATIBLE SWITCH INVALIDATES EVERYTHING, which is the conservative arm and the one the incumbent
        /// pays on every switch for want of dedup.
        /// </summary>
        [Fact]
        public void AnIncompatibleSwitch_InvalidatesEverySlot()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet texture = harness.Set("SpriteBatch.texture");
            VulkanResourceSet uniform = harness.Set("Beam");

            VulkanBoundPipeline before = harness.PipelineFor(uniform, texture);
            VulkanBoundPipeline after = harness.PipelineFor(texture, uniform);

            var counts = new VulkanCmdCallCounts();
            var sink = new VulkanCountingCmdSink(counts);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);

            records.SetPipelineLayout(before.Layout, before.SetLayouts);
            records.Record(0, uniform, 0);
            records.Record(1, texture, 0);
            records.Flush(ref sink);

            Assert.Equal(0, records.SetPipelineLayout(after.Layout, after.SetLayouts));
            Assert.True(records.IsDirty(0));
            Assert.True(records.IsDirty(1));
        }

        /// <summary>
        /// A REBIND OF THE LAYOUT ALREADY CURRENT DOES NOTHING, which is the fork's pipeline-identity guard in the
        /// seat that matters here. Two pipelines built from the same set layouts SHARE one <c>VkPipelineLayout</c>
        /// under V-D5, so switching between them is this case rather than a compatible-prefix computation, and it
        /// must not dirty anything.
        /// </summary>
        [Fact]
        public void ARebindOfTheLayoutAlreadyCurrent_DirtiesNothing()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet set = harness.Set("Beam");

            // Two pipelines, one shared layout: "Trail" and "Overlay" are one content, and so is "Beam" with
            // itself through two separately created layout objects.
            VulkanBoundPipeline first = harness.Pipeline("Beam");
            VulkanBoundPipeline second = harness.Pipeline("Beam");
            Assert.Equal(first.Layout, second.Layout);

            var counts = new VulkanCmdCallCounts();
            var sink = new VulkanCountingCmdSink(counts);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);

            records.SetPipelineLayout(first.Layout, first.SetLayouts);
            records.Record(0, set, 0);
            records.Flush(ref sink);

            Assert.Equal(1, records.SetPipelineLayout(second.Layout, second.SetLayouts));
            Assert.False(records.IsDirty(0));

            records.Flush(ref sink);
            Assert.Equal(1, counts.BindDescriptorSetCalls);
        }

        /// <summary>
        /// WITH NO PIPELINE PREVIOUSLY BOUND EVERY RECORDED SLOT IS INVALIDATED, and that is correct rather than
        /// merely conservative: nothing is bound, so nothing survives.
        /// </summary>
        [Fact]
        public void TheFirstPipelineBound_InvalidatesEveryRecordedSlot()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet set = harness.Set("Beam");
            VulkanBoundPipeline pipeline = harness.PipelineFor(set);

            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.Record(0, set, 0);

            Assert.Equal(0, records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts));
            Assert.True(records.IsDirty(0));
        }

        /// <summary>A null <c>VkPipelineLayout</c> is refused by name: a pipeline declaring no sets still has a
        /// real, empty pipeline layout, so a zero handle means the pipeline was built without one.</summary>
        [Fact]
        public void ANullPipelineLayout_IsRefusedByName()
        {
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);

            Assert.Throws<ArgumentOutOfRangeException>(() => records.SetPipelineLayout(0, []));
        }

        // ---- V-R7's other half: the validation-build draw assertion ----

        /// <summary>
        /// UNDER <c>KE_VULKAN_VALIDATION</c> THE FLUSH ASSERTS EVERY BOUND SET'S LAYOUT IS THE PIPELINE LAYOUT'S
        /// SET LAYOUT AT THAT INDEX. This is V-R7's second guard, the one that runs where a draw would consume the
        /// prefix rather than where a test computes it, and it is what would catch a prefix computation that had
        /// gone wrong in the unsafe direction on a real device.
        /// </summary>
        [Fact]
        public void UnderValidation_ASetThatDoesNotSatisfyItsSlot_IsRefusedAtTheFlush()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet declared = harness.Set("Beam");
            VulkanResourceSet wrong = harness.Set("SpriteBatch.texture");
            VulkanBoundPipeline pipeline = harness.PipelineFor(declared);

            var counts = new VulkanCmdCallCounts();
            var sink = new VulkanCountingCmdSink(counts);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics, assertsBoundSetLayouts: true);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            records.Record(0, declared, 0);
            records.Flush(ref sink);
            Assert.Equal(1, counts.BindDescriptorSetCalls);

            records.Record(0, wrong, 0);

            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
                () => records.Flush(ref sink));

            Assert.Contains("V-R7", refused.Message, StringComparison.Ordinal);
            Assert.Contains("KE_VULKAN_VALIDATION", refused.Message, StringComparison.Ordinal);
            Assert.Equal(1, counts.BindDescriptorSetCalls);
        }

        /// <summary>
        /// AND WITHOUT VALIDATION THE SAME BIND GOES STRAIGHT THROUGH, which is what makes the assertion a
        /// diagnostic rather than a behaviour change. A per-bind loop over the run belongs on the run that asked
        /// for validation, and a run that did not has already accepted this class of risk everywhere else.
        /// </summary>
        [Fact]
        public void WithoutValidation_TheSameBindIsNotChecked()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet declared = harness.Set("Beam");
            VulkanResourceSet wrong = harness.Set("SpriteBatch.texture");
            VulkanBoundPipeline pipeline = harness.PipelineFor(declared);

            var counts = new VulkanCmdCallCounts();
            var sink = new VulkanCountingCmdSink(counts);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            records.Record(0, wrong, 0);
            records.Flush(ref sink);

            Assert.Equal(1, counts.BindDescriptorSetCalls);
            Assert.False(records.AssertsBoundSetLayouts);
        }

        /// <summary>
        /// AND A CORRECT FRAME PASSES THE ASSERTION AT EVERY BIND, which is the half an assertion test most needs:
        /// a check that refused everything would pass the test above forever.
        /// </summary>
        [Fact]
        public void UnderValidation_EveryShippedPipelineShapeBindsCleanly()
        {
            using var harness = new VulkanBindHarness();
            var counts = new VulkanCmdCallCounts();
            var sink = new VulkanCountingCmdSink(counts);

            foreach ((string _, string[] slots) in VulkanDescriptorLimitTests.ShippedPipelines)
            {
                var sets = new VulkanResourceSet[slots.Length];
                for (int i = 0; i < slots.Length; i++) sets[i] = harness.Set(slots[i]);

                VulkanBoundPipeline pipeline = harness.PipelineFor(sets);
                var records = new VulkanBindRecords(PipelineBindPoint.Graphics, assertsBoundSetLayouts: true);
                records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

                for (uint slot = 0; slot < (uint)sets.Length; slot++) records.Record(slot, sets[slot], 0);
                records.Flush(ref sink);
            }

            Assert.Equal(VulkanDescriptorLimitTests.ShippedPipelines.Count, counts.BindDescriptorSetCalls);
        }

        /// <summary>And a command list built by a device with validation on arms it, which is the wiring rather
        /// than the rule.</summary>
        [Fact]
        public void ACommandListBuiltWithValidation_ArmsBothArms()
        {
            using var harness = new VulkanBindHarness();

            using var armed = new VulkanCommandList(
                new VulkanCommandPoolRing(harness.Fixture.CommandApi, 3, harness.Fixture.Timeline,
                    harness.Fixture.Backpressure),
                harness.Fixture.Retired, uploads: null, assertBoundSetLayouts: true);

            Assert.True(armed.GraphicsBinds.AssertsBoundSetLayouts);
            Assert.True(armed.ComputeBinds.AssertsBoundSetLayouts);

            using VulkanCommandList plain = harness.Fixture.CreateList();
            Assert.False(plain.GraphicsBinds.AssertsBoundSetLayouts);
        }

        // ---- Fixtures ----

        // Every shipped pipeline, as its slot names and its real deduplicated pipeline layout.
        static IReadOnlyList<(string Name, string[] Slots, VulkanBoundPipeline Bound)> Build(
            VulkanBindHarness harness)
            => VulkanDescriptorLimitTests.ShippedPipelines
                .Select(p => (Name: p.Pipeline, Slots: p.Slots, Bound: harness.Pipeline(p.Slots)))
                .ToArray();

        // THE REFERENCE, computed from CONTENT rather than from handles: how many leading slots of the two
        // pipelines declare set layouts vkCreateDescriptorSetLayout would read identically. This is what "compatible
        // for set N" means in the specification, and comparing the backend's handle answer against it is what makes
        // the guard a guard rather than a restatement.
        static int TruePrefix(string[] outgoing, string[] incoming)
        {
            int shared = Math.Min(outgoing.Length, incoming.Length);

            int prefix = 0;
            while (prefix < shared && SameContent(outgoing[prefix], incoming[prefix])) prefix++;

            return prefix;
        }

        static bool SameContent(string left, string right)
            => BindingsOf(left).SequenceEqual(BindingsOf(right));

        static VulkanDescriptorBinding[] BindingsOf(string shipped)
            => VulkanDescriptorPolicy.BindingsFor(VulkanDescriptorLimitTests.ShippedLayouts[shipped]);
    }
}
