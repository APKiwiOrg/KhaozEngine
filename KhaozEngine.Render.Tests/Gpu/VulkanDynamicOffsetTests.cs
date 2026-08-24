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
    /// THE COMPOSED POSITIONAL <c>pDynamicOffsets</c> ARRAY (section 6.2), device-free, over every shipped layout
    /// shape and at every frame slot. Work-breakdown row 11
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/521).
    ///
    /// <para><b>WHY THIS EXISTS AS ITS OWN FILE.</b> An off-by-one in a positional array reads the wrong slice of
    /// the RIGHT buffer, so it renders plausible garbage rather than throwing. Nothing else in the backend fails
    /// that way, and no golden on a software rasterizer reliably catches "the shadow cascade read cascade 2's
    /// matrix". The array carries no key and no name: POSITION is the only thing that says which entry belongs to
    /// which descriptor.</para>
    ///
    /// <para><b>AND THE ARITHMETIC IS THE ONE A VUID MEASURES.</b>
    /// <c>VUID-vkCmdBindDescriptorSets-pDescriptorSets-01979</c> requires the effective offset composed here plus
    /// the descriptor's range to stay inside the buffer, against the range row 10
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/520) wrote and the stride row 8
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/518) owns. Those three have to agree or validation fails on
    /// the LAST FRAME SLOT ONLY, which is why every sweep below walks all of them rather than the one a test
    /// happened to open on.</para>
    /// </summary>
    public sealed class VulkanDynamicOffsetTests
    {
        const GpuShaderStages V = GpuShaderStages.Vertex;
        const GpuShaderStages F = GpuShaderStages.Fragment;

        /// <summary>
        /// EVERY SHIPPED LAYOUT SHAPE, AT EVERY FRAME SLOT: each composed entry is exactly
        /// <c>ringBase + rangeOffset</c> for that descriptor's own ring, and the entry plus the descriptor's RANGE
        /// stays inside that ring's whole allocation. That second half is the VUID, asserted against row 10's
        /// written range and row 8's stride rather than against a number this test invented.
        /// <para>
        /// The count is asserted too, per set: one entry per uniform buffer element and none for anything else,
        /// which is what makes the array positional in the first place.
        /// </para>
        /// </summary>
        [Fact]
        public void EveryShippedShape_ComposesAnOffsetThatKeepsItsRangeInsideTheBuffer()
        {
            using var harness = new VulkanBindHarness(framesInFlight: 3);

            var shapes = new List<(string Name, VulkanResourceSet Set)>();
            foreach (string name in VulkanDescriptorLimitTests.ShippedLayouts.Keys)
            {
                shapes.Add((name, harness.Set(name)));
            }

            Assert.Equal(35, shapes.Count);   // 35 since #604 split the splat pipeline's layout in two

            for (int segment = 0; segment < harness.Rings.FramesInFlight; segment++)
            {
                Assert.Equal(segment, harness.Rings.CurrentSegment);

                foreach ((string name, VulkanResourceSet set) in shapes)
                {
                    VulkanBoundPipeline pipeline = harness.PipelineFor(set);
                    var binds = new List<VulkanRecordedBind>();
                    var sink = new VulkanCapturingCmdSink(binds);
                    var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
                    records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

                    records.Record(0, set, 0);
                    records.Flush(ref sink);

                    uint[] offsets = Assert.Single(binds).DynamicOffsets;
                    VulkanDynamicUniform[] dynamics = set.DynamicUniforms.ToArray();

                    Assert.Equal(dynamics.Length, offsets.Length);
                    Assert.Equal(set.Layout.DynamicUniformCount, offsets.Length);

                    for (int i = 0; i < dynamics.Length; i++)
                    {
                        VulkanDynamicUniform dynamicUniform = dynamics[i];
                        ulong expected = dynamicUniform.Ring.FrameBaseBytes(segment) + dynamicUniform.RangeOffset;

                        Assert.Equal(expected, offsets[i]);

                        // THE VUID ITSELF, at this frame slot: effective offset plus range inside the buffer.
                        Assert.True(offsets[i] + dynamicUniform.Range <= dynamicUniform.Ring.TotalBytes,
                            $"{name} binding {dynamicUniform.Binding} composes offset {offsets[i]} with range "
                            + $"{dynamicUniform.Range} against a {dynamicUniform.Ring.TotalBytes}-byte allocation "
                            + $"at frame slot {segment}.");
                    }
                }

                harness.Rings.BeginFrame();
            }
        }

        /// <summary>
        /// THE LAST FRAME SLOT WITH A REAL CALLER OFFSET, which is the case that fails and the only one that does.
        /// A set built the way the five shipped renderers that pass a non-zero dynamic offset build theirs (one
        /// slot of a many-slot buffer as the descriptor's range), bound at the LAST slot of the buffer, on the LAST
        /// frame segment: the composed offset plus the range lands exactly on the end of the allocation and not one
        /// byte past it.
        /// <para>
        /// A range of the STRIDE instead of the bind window would overrun here by exactly the caller's own offset,
        /// which is the shape V-M6 exists to refuse and the reason row 10 and this row have to agree.
        /// </para>
        /// </summary>
        [Fact]
        public void TheLastSlotOfTheLastSegment_LandsExactlyOnTheEndOfTheBuffer()
        {
            const uint slotBytes = 256;
            const uint slots = 4;

            using var harness = new VulkanBindHarness(framesInFlight: 3);
            VulkanResourceSet set = harness.WindowedSet(slotBytes, slots);
            VulkanBoundPipeline pipeline = harness.PipelineFor(set);

            VulkanDynamicUniform dynamicUniform = Assert.Single(set.DynamicUniforms.ToArray());
            Assert.True(dynamicUniform.AppliesCallerOffset);
            Assert.Equal(slotBytes, dynamicUniform.Range);

            for (int segment = 0; segment < harness.Rings.FramesInFlight; segment++)
            {
                for (uint slot = 0; slot < slots; slot++)
                {
                    var binds = new List<VulkanRecordedBind>();
                    var sink = new VulkanCapturingCmdSink(binds);
                    var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
                    records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

                    records.Record(0, set, slot * slotBytes);
                    records.Flush(ref sink);

                    uint offset = Assert.Single(Assert.Single(binds).DynamicOffsets);

                    Assert.Equal(dynamicUniform.Ring.FrameBaseBytes(segment) + slot * slotBytes, offset);
                    Assert.True(offset + dynamicUniform.Range <= dynamicUniform.Ring.TotalBytes);
                }

                harness.Rings.BeginFrame();
            }

            // The tightest case, spelled out: last segment, last slot, exactly on the end.
            ulong lastBase = dynamicUniform.Ring.FrameBaseBytes(harness.Rings.FramesInFlight - 1);
            Assert.Equal(dynamicUniform.Ring.TotalBytes,
                lastBase + (slots - 1) * slotBytes + dynamicUniform.Range);
        }

        /// <summary>
        /// AND AN OFFSET THAT WOULD LEAVE ITS OWN SEGMENT IS REFUSED AT THE FLUSH, by name, rather than composed
        /// and handed to the driver. Row 10 states the same invariant at set creation with a caller offset of zero,
        /// because zero is all it can know there. This is where a real caller offset arrives, and it is the only
        /// place the invariant can actually fail.
        /// </summary>
        [Fact]
        public void AnOffsetThatLeavesItsSegment_IsRefusedAtTheFlush()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet set = harness.WindowedSet(slotBytes: 256, slots: 4);
            VulkanBoundPipeline pipeline = harness.PipelineFor(set);

            var counts = new VulkanCmdCallCounts();
            var sink = new VulkanCountingCmdSink(counts);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            records.Record(0, set, 4 * 256);

            ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(
                () => records.Flush(ref sink));

            Assert.Contains("VUID-vkCmdBindDescriptorSets-pDescriptorSets-01979", refused.Message,
                StringComparison.Ordinal);
            Assert.Equal(0, counts.BindDescriptorSetCalls);

            // AND THE SLOT IS STILL DIRTY. Clearing the mark before the call landed would turn a loud, repeatable
            // refusal into one throw followed by a draw that renders against whatever the descriptor slots hold.
            Assert.True(records.IsDirty(0));
        }

        /// <summary>
        /// AND AN ENTRY THAT IS NOT A MULTIPLE OF THE DYNAMIC-OFFSET ALIGNMENT IS REFUSED AT THE FLUSH, by name.
        /// <c>VUID-vkCmdBindDescriptorSets-pDynamicOffsets-01971</c> is the other rule this array answers to, and
        /// only the ring base satisfies it by construction: the caller's own term holds it because every shipped
        /// slot size is 256-aligned, which is an invariant the renderers obey rather than one the ring enforces.
        /// A 128-byte offset is exactly the shape that quietly breaks it.
        /// </summary>
        [Fact]
        public void AnEntryThatIsNotAMultipleOfTheAlignment_IsRefusedAtTheFlush()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet set = harness.WindowedSet(slotBytes: 256, slots: 4);
            VulkanBoundPipeline pipeline = harness.PipelineFor(set);

            var counts = new VulkanCmdCallCounts();
            var sink = new VulkanCountingCmdSink(counts);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            // Inside its own segment, so the V-M6 window refusal has nothing to say about it. 128 is simply not a
            // multiple of the 256 the entry owes.
            records.Record(0, set, 128);

            ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(
                () => records.Flush(ref sink));

            Assert.Contains("VUID-vkCmdBindDescriptorSets-pDynamicOffsets-01971", refused.Message,
                StringComparison.Ordinal);
            Assert.Equal(0, counts.BindDescriptorSetCalls);

            // AND THE SLOT IS STILL DIRTY, so the refusal repeats at the next draw rather than being spent on the
            // first one and followed by a draw against whatever the descriptor slots hold.
            Assert.True(records.IsDirty(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => records.Flush(ref sink));
            Assert.Equal(0, counts.BindDescriptorSetCalls);
        }

        /// <summary>
        /// THE SAME SHAPE WITH AN ALIGNED OFFSET FLUSHES, which is what makes the refusal above a statement about
        /// the OFFSET rather than about the set. One slot further along the same buffer, and the entry lands on a
        /// multiple of the alignment because the slot size is one.
        /// </summary>
        [Fact]
        public void TheSameShapeWithAnAlignedOffset_Flushes()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet set = harness.WindowedSet(slotBytes: 256, slots: 4);
            VulkanBoundPipeline pipeline = harness.PipelineFor(set);

            var binds = new List<VulkanRecordedBind>();
            var sink = new VulkanCapturingCmdSink(binds);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            records.Record(0, set, 256);
            records.Flush(ref sink);

            VulkanDynamicUniform dynamicUniform = Assert.Single(set.DynamicUniforms.ToArray());
            uint offset = Assert.Single(Assert.Single(binds).DynamicOffsets);

            Assert.Equal(dynamicUniform.Ring.CurrentFrameBaseBytes + 256, offset);
            Assert.Equal(0ul, offset % dynamicUniform.Ring.OffsetAlignmentBytes);
            Assert.False(records.IsDirty(0));
        }

        /// <summary>
        /// AND THE CHECK IS ON THE ENTRY, NOT ON ITS TERMS, which is what the VUID actually measures. A range
        /// offset and a caller offset that are each misaligned but SUM to an aligned entry compose a legal bind,
        /// and refusing it would refuse something the driver accepts.
        /// </summary>
        [Fact]
        public void TwoMisalignedTermsThatSumToAnAlignedEntry_Compose()
        {
            using var harness = new VulkanBindHarness(framesInFlight: 3);

            IGpuBuffer buffer = harness.UniformBuffer(1024);
            VulkanResourceSet set = harness.CustomSet(
                VulkanResourceFixture.UniformLayout(dynamic: true), new GpuBufferRange(buffer, 128, 256));
            VulkanBoundPipeline pipeline = harness.PipelineFor(set);

            // Past segment zero, so the base is a real number rather than the one value every alignment divides.
            harness.Rings.BeginFrame();
            harness.Rings.BeginFrame();

            VulkanDynamicUniform dynamicUniform = Assert.Single(set.DynamicUniforms.ToArray());
            ulong alignment = dynamicUniform.Ring.OffsetAlignmentBytes;

            // Both terms really are misaligned, so the sum below is the only reason this composes at all.
            Assert.NotEqual(0ul, dynamicUniform.RangeOffset % alignment);
            Assert.NotEqual(0ul, 128ul % alignment);

            var binds = new List<VulkanRecordedBind>();
            var sink = new VulkanCapturingCmdSink(binds);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            records.Record(0, set, 128);
            records.Flush(ref sink);

            uint offset = Assert.Single(Assert.Single(binds).DynamicOffsets);

            Assert.Equal(dynamicUniform.Ring.CurrentFrameBaseBytes + 256, offset);
            Assert.Equal(0ul, offset % alignment);
        }

        /// <summary>
        /// AND A RUN WHOSE ENTRIES ARE ALL ALIGNED NEVER MEETS THE REFUSAL AT ALL. The same three-set run the
        /// positional test uses, at a non-zero frame slot with a real caller offset: one call, and every entry a
        /// multiple of its own ring's alignment. A check that fired on the shipped shapes would be worse than no
        /// check, so this is the half that pins it does not.
        /// </summary>
        [Fact]
        public void AcrossARunOfAlignedEntries_TheAlignmentRefusalNeverFires()
        {
            using var harness = new VulkanBindHarness(framesInFlight: 3);
            VulkanResourceSet named = harness.WindowedSet(slotBytes: 256, slots: 4);
            VulkanResourceSet unnamed = harness.Set("Beam");
            VulkanResourceSet textureOnly = harness.Set("SpriteBatch.texture");
            VulkanBoundPipeline pipeline = harness.PipelineFor(named, unnamed, textureOnly);

            harness.Rings.BeginFrame();
            harness.Rings.BeginFrame();

            var binds = new List<VulkanRecordedBind>();
            var sink = new VulkanCapturingCmdSink(binds);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            records.Record(0, named, 768);
            records.Record(1, unnamed, 0);
            records.Record(2, textureOnly, 0);
            records.Flush(ref sink);

            VulkanRecordedBind bind = Assert.Single(binds);
            VulkanDynamicUniform[] dynamics =
            [
                .. named.DynamicUniforms.ToArray(), .. unnamed.DynamicUniforms.ToArray(),
                .. textureOnly.DynamicUniforms.ToArray(),
            ];

            Assert.Equal(dynamics.Length, bind.DynamicOffsets.Length);
            for (int i = 0; i < dynamics.Length; i++)
            {
                Assert.Equal(0ul, bind.DynamicOffsets[i] % dynamics[i].Ring.OffsetAlignmentBytes);
            }
        }

        /// <summary>
        /// SET ORDER THEN BINDING ORDER, ACROSS A RUN, INCLUDING RING BASES FOR UNIFORM BUFFERS THE CALLER NEVER
        /// NAMED. This is the whole positional hazard in one assertion: the run's first set carries the caller's
        /// offset, the second carries a uniform the caller said nothing about, and the second entry still has to be
        /// there and still has to be that ring's base. Dropping it would shift every later entry onto the wrong
        /// descriptor.
        /// </summary>
        [Fact]
        public void AcrossARun_TheArrayIsSetThenBindingOrderAndCoversSetsTheCallerNeverNamed()
        {
            using var harness = new VulkanBindHarness(framesInFlight: 3);
            VulkanResourceSet named = harness.WindowedSet(slotBytes: 256, slots: 4);
            VulkanResourceSet unnamed = harness.Set("Beam");
            VulkanResourceSet textureOnly = harness.Set("SpriteBatch.texture");
            VulkanBoundPipeline pipeline = harness.PipelineFor(named, unnamed, textureOnly);

            // Past segment zero, so a base of zero cannot pass by accident.
            harness.Rings.BeginFrame();
            harness.Rings.BeginFrame();

            var binds = new List<VulkanRecordedBind>();
            var sink = new VulkanCapturingCmdSink(binds);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            records.Record(0, named, 512);
            records.Record(1, unnamed, 0);
            records.Record(2, textureOnly, 0);
            records.Flush(ref sink);

            VulkanRecordedBind bind = Assert.Single(binds);
            Assert.Equal(3, bind.Sets.Length);

            VulkanDynamicUniform namedUniform = Assert.Single(named.DynamicUniforms.ToArray());
            VulkanDynamicUniform unnamedUniform = Assert.Single(unnamed.DynamicUniforms.ToArray());

            // TWO entries for THREE sets: the texture-and-sampler set contributes none, and the entry for the set
            // nobody passed an offset for is still present, carrying its ring base.
            Assert.Equal(
                new[]
                {
                    (uint)(namedUniform.Ring.CurrentFrameBaseBytes + 512),
                    (uint)unnamedUniform.Ring.CurrentFrameBaseBytes,
                },
                bind.DynamicOffsets);

            Assert.NotEqual(0u, bind.DynamicOffsets[1]);
        }

        /// <summary>
        /// BINDING ORDER WITHIN ONE SET, WITH THE CALLER'S OFFSET ON EXACTLY ONE ELEMENT. No shipped pipeline
        /// spends more than one dynamic uniform descriptor, so this shape is synthetic on purpose: it is precisely
        /// the case the shipped table cannot reach and precisely the one a positional off-by-one shows up in.
        /// <para>
        /// The two uniform buffers are DIFFERENT SIZES so their segment strides differ, which is what makes their
        /// bases distinguishable past segment zero. Two 256-byte buffers would compose two identical entries and
        /// the ordering would be unobservable.
        /// </para>
        /// </summary>
        [Fact]
        public void WithinOneSet_TheOrderIsBindingOrderAndOnlyTheDeclaredElementTakesTheCallerOffset()
        {
            using var harness = new VulkanBindHarness(framesInFlight: 3);

            IGpuBuffer first = harness.UniformBuffer(512);
            IGpuTexture between = harness.SampledTexture();
            IGpuBuffer second = harness.UniformBuffer(1024);

            VulkanResourceSet set = harness.CustomSet(
                new GpuResourceLayoutDescription(
                    new GpuResourceLayoutElement("First", GpuResourceKind.UniformBuffer, V),
                    new GpuResourceLayoutElement("Between", GpuResourceKind.TextureReadOnly, F),
                    new GpuResourceLayoutElement("Second", GpuResourceKind.UniformBuffer, F, dynamic: true)),
                first, between, new GpuBufferRange(second, 0, 256));

            VulkanBoundPipeline pipeline = harness.PipelineFor(set);

            harness.Rings.BeginFrame();
            harness.Rings.BeginFrame();

            var binds = new List<VulkanRecordedBind>();
            var sink = new VulkanCapturingCmdSink(binds);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            records.Record(0, set, 768);
            records.Flush(ref sink);

            VulkanDynamicUniform[] dynamics = set.DynamicUniforms.ToArray();
            Assert.Equal(2, dynamics.Length);
            Assert.Equal(0u, dynamics[0].Binding);
            Assert.Equal(2u, dynamics[1].Binding);
            Assert.False(dynamics[0].AppliesCallerOffset);
            Assert.True(dynamics[1].AppliesCallerOffset);

            Assert.Equal(
                new[]
                {
                    (uint)dynamics[0].Ring.CurrentFrameBaseBytes,
                    (uint)(dynamics[1].Ring.CurrentFrameBaseBytes + 768),
                },
                Assert.Single(binds).DynamicOffsets);

            // The two bases really are different numbers, so the ordering above is observable rather than lucky.
            Assert.NotEqual(dynamics[0].Ring.CurrentFrameBaseBytes, dynamics[1].Ring.CurrentFrameBaseBytes);
        }

        /// <summary>
        /// THE ARRAY IS RECOMPOSED PER BIND, WHICH IS THE INCUMBENT'S OWN BUG NOT BEING INHERITED. Its batching
        /// flush resets the batch count and the first set but NOT the accumulated dynamic-offset count, so a SECOND
        /// batch inside ONE flush passes a too-large count built from stale entries. Two runs in one flush, each
        /// carrying its own sets' dynamic descriptors and no more.
        /// </summary>
        [Fact]
        public void TwoRunsInOneFlush_EachCarryOnlyTheirOwnOffsets()
        {
            using var harness = new VulkanBindHarness();
            VulkanResourceSet[] sets =
            [
                harness.Set("Beam"), harness.Set("Distortion"), harness.Set("SpriteBatch.texture"),
                harness.Set("Sky"), harness.Set("Trail"),
            ];
            VulkanBoundPipeline pipeline = harness.PipelineFor(sets);

            var binds = new List<VulkanRecordedBind>();
            var sink = new VulkanCapturingCmdSink(binds);
            var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
            records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

            for (uint slot = 0; slot < 5; slot++) records.Record(slot, sets[slot], 0);
            records.Flush(ref sink);
            binds.Clear();

            // Slots 0 and 1 move, slot 2 does not, slots 3 and 4 move. Two runs in ONE flush.
            records.Record(0, sets[3], 0);
            records.Record(1, sets[0], 0);
            records.Record(3, sets[1], 0);
            records.Record(4, sets[0], 0);
            records.Flush(ref sink);

            Assert.Equal(2, binds.Count);

            // Two uniform buffers in each run's two sets, and NOT four in the second because the first run's
            // entries were never cleared.
            Assert.Equal(2, binds[0].DynamicOffsets.Length);
            Assert.Equal(2, binds[1].DynamicOffsets.Length);
        }

        /// <summary>
        /// THE INVARIANT AS ONE RULE OVER A WHOLE FRAME'S WORTH OF BINDS: the count passed to every
        /// <c>vkCmdBindDescriptorSets</c> equals the sum of THAT call's sets' dynamic descriptor counts. This is
        /// the assertion the design names as what pins the incumbent's defect, stated over the shipped shapes
        /// rather than over one contrived pair.
        /// </summary>
        [Fact]
        public void AcrossEveryShippedPipelineShape_TheOffsetCountEqualsTheRunsDynamicDescriptors()
        {
            using var harness = new VulkanBindHarness();

            var byHandle = new Dictionary<ulong, int>();
            var binds = new List<VulkanRecordedBind>();
            var sink = new VulkanCapturingCmdSink(binds);

            foreach (string name in VulkanDescriptorLimitTests.ShippedLayouts.Keys)
            {
                VulkanResourceSet set = harness.Set(name);
                byHandle[set.DescriptorSet] = set.Layout.DynamicUniformCount;

                VulkanBoundPipeline pipeline = harness.PipelineFor(set, set);
                var records = new VulkanBindRecords(PipelineBindPoint.Graphics);
                records.SetPipelineLayout(pipeline.Layout, pipeline.SetLayouts);

                records.Record(0, set, 0);
                records.Record(1, set, 0);
                records.Flush(ref sink);
            }

            Assert.NotEmpty(binds);
            foreach (VulkanRecordedBind bind in binds)
            {
                Assert.Equal(bind.Sets.Sum(handle => byHandle[handle]), bind.DynamicOffsets.Length);
            }
        }
    }
}
