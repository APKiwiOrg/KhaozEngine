using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE BIND SCHEDULE, DECISIONS M-R5 TO M-R9, WITH NO DEVICE ANYWHERE. Section 6.3 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>, work-breakdown row 13
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/579).
    ///
    /// <para>
    /// WHAT THIS FILE IS FOR, and why the split from <c>MetalBindFlushTests</c>. This one drives the STATE
    /// MACHINE: what a record does, when a slot is owed a bind, what a pipeline switch invalidates, and what is
    /// refused. That one drives the EMISSION: which calls come out, at which indices, with which offsets. The
    /// two fail differently and a file that mixed them would report a wrong index as a wrong state.
    /// </para>
    /// <para>
    /// "WE RE-ACTIVATED WHEN WE DID NOT NEED TO" AND "WE FAILED TO RE-ACTIVATE WHEN WE DID" ARE BOTH INVISIBLE IN
    /// A GREEN SUITE OTHERWISE, which is the design's own reason for asking for these device-free rather than
    /// leaving them to a golden. The second one is a corruption no golden reaches at all, because the goldens do
    /// not restart a render pass mid-scene.
    /// </para>
    /// </summary>
    public sealed class MetalBindRecordsTests
    {
        [Fact]
        public void ARecordEmitsNothingAtAll()
        {
            using var harness = new MetalRingHarness();
            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            var calls = new FakeMetalEncoderCalls();

            records.SetIndexTable(MetalBindProgram.Table());
            records.Record(0, MetalBindProgram.Set(harness), 0);

            Assert.Equal(0, calls.ArgumentTableWrites);
            Assert.True(records.IsDirty(0));
            Assert.Equal(1, records.RecordedSlotCount);
        }

        /// <summary>
        /// CLAUSE 7. Several records between two draws collapse to ONE flush, and a re-record of what is already
        /// there does not mark a clean slot dirty at all. Both halves matter: the first is what stops a frame
        /// being O(n squared) in its rebinds, and the second is what makes a renderer that re-binds its set every
        /// draw cost nothing.
        /// </summary>
        [Fact]
        public void RepeatedRecordsBetweenTwoDrawsCollapseToOneFlush()
        {
            using var harness = new MetalRingHarness();
            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            MetalBoundSet set = MetalBindProgram.Set(harness);
            records.SetIndexTable(MetalBindProgram.Table());

            for (int i = 0; i < 50; i++) records.Record(0, set, 0);

            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            int afterFirstDraw = calls.ArgumentTableWrites;
            Assert.True(afterFirstDraw > 0);
            Assert.False(records.IsDirty(0));

            // A SECOND DRAW WITH THE SAME BINDS EMITS NOTHING, and neither does re-recording the same set fifty
            // more times in between.
            for (int i = 0; i < 50; i++) records.Record(0, set, 0);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            Assert.Equal(afterFirstDraw, calls.ArgumentTableWrites);

            // Rule 7 in one number: the record follows the highest SLOT and never the count of rebinds.
            Assert.Equal(1, records.RecordedSlotCount);
            Assert.Equal(4, records.SlotCapacity);
        }

        /// <summary>
        /// CLAUSE 6. A slot recorded as holding no set is SKIPPED rather than unbound: Metal's argument tables
        /// are absolute, so nothing this flush does not write keeps what is there. What the null record must
        /// still do is forget what it emitted, so the NEXT bind of the same set is a full rebind rather than an
        /// offsets-only call against entries another slot may have overwritten meanwhile.
        /// </summary>
        [Fact]
        public void ASlotWhoseSetWentNullIsSkippedAndForgetsWhatItEmitted()
        {
            using var harness = new MetalRingHarness();
            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            MetalBoundSet set = MetalBindProgram.Set(harness);
            records.SetIndexTable(MetalBindProgram.Table());

            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            records.Record(0, set, 0);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);
            Assert.True(records.IsEmittedIn(0, Epoch));

            records.Record(0, default, 0);
            int before = calls.ArgumentTableWrites;
            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            Assert.Equal(before, calls.ArgumentTableWrites);
            Assert.False(records.IsDirty(0));
            Assert.False(records.IsEmittedIn(0, Epoch));

            // AND THE RETURN IS A FULL REBIND rather than an offsets-only call: the slot forgot.
            records.Record(0, set, 0);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            Assert.Empty(calls.OffsetWrites);
            Assert.Equal(before * 2, calls.ArgumentTableWrites);
        }

        /// <summary>
        /// CLAUSE 5, THE HALF THAT COSTS NOTHING (M-R9). Two pipelines whose programs map every element to the
        /// same index invalidate NOTHING on a switch, because Metal's argument tables are absolute and per
        /// encoder so the bound resources are still there to keep. Row 10 measured what that buys over the
        /// shipped catalog: 25 of 42 programs share a table with an earlier one, so this is the shadow pass and
        /// the post chain rather than a rare coincidence.
        /// </summary>
        [Fact]
        public void APipelineSwitchToATableWithTheSameIndicesInvalidatesNothing()
        {
            using var harness = new MetalRingHarness();
            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            var cache = new MetalIndexTableCache();

            MetalShaderIndexTable first = cache.Canonical(MetalBindProgram.Table());
            MetalShaderIndexTable second = cache.Canonical(MetalBindProgram.Table());

            // The positive control: two independent builds really are two objects, and the cache is what makes
            // the comparison a handle compare that can answer yes.
            Assert.Same(first, second);

            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            Assert.True(records.SetIndexTable(first));
            records.Record(0, MetalBindProgram.Set(harness), 0);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            int afterFirst = calls.ArgumentTableWrites;

            Assert.False(records.SetIndexTable(second));
            Assert.False(records.IsDirty(0));

            records.Flush(ref sink, Encoder, Epoch, segment: 0);
            Assert.Equal(afterFirst, calls.ArgumentTableWrites);
        }

        /// <summary>
        /// CLAUSE 5, THE HALF THAT COSTS EVERYTHING. A switch to a program whose table maps elements differently
        /// invalidates every recorded slot, and it must clear the EPOCH STAMP rather than only mark dirty: the
        /// incoming program expects each element at a different index, so an offsets-only rebind would move an
        /// offset on a binding it never reads and leave the ones it does read pointing at the old program's
        /// resources.
        /// </summary>
        [Fact]
        public void APipelineSwitchToADifferentTableForcesAFullRebindAndNotAnOffsetsOnlyCall()
        {
            using var harness = new MetalRingHarness();
            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            MetalBoundSet set = MetalBindProgram.Set(harness);

            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            records.SetIndexTable(MetalBindProgram.Table());
            records.Record(0, set, 0);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            int afterFirst = calls.ArgumentTableWrites;

            // A DIFFERENT INSTANCE IS A DIFFERENT TABLE, which is what SameIndicesAs answers and which is the
            // safe direction for a table built outside the cache: invalidate too much rather than too little.
            Assert.True(records.SetIndexTable(MetalBindProgram.Table()));
            Assert.True(records.IsDirty(0));
            Assert.False(records.IsEmittedIn(0, Epoch));

            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            Assert.Empty(calls.OffsetWrites);
            Assert.Equal(afterFirst * 2, calls.ArgumentTableWrites);
        }

        /// <summary>
        /// A NON-ZERO OFFSET AGAINST A SET THAT DECLARES NO DYNAMIC ELEMENT IS REFUSED, because nothing would
        /// carry it. The composition adds the caller's offset only where the element was declared dynamic, so
        /// this offset would be silently dropped and the draw would read the buffer's first slot, which is a
        /// wrong render with no error attached.
        /// </summary>
        [Fact]
        public void ANonZeroOffsetAgainstASetWithNoDynamicElementIsRefused()
        {
            using var harness = new MetalRingHarness();
            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            MetalBuffer buffer = harness.NewBuffer(64, KhaozEngine.Gpu.GpuBufferUsage.UniformBuffer);

            MetalBoundSet nothingDynamic = MetalBindProgram.Set(
                new MetalBoundResource(MetalIndexSpace.Buffer, buffer, 0, 64, AppliesCallerOffset: false));

            ArgumentException thrown = Assert.Throws<ArgumentException>(
                () => records.Record(0, nothingDynamic, 256));

            Assert.Contains("declares no dynamic element", thrown.Message, StringComparison.Ordinal);

            // A ZERO OFFSET IS ACCEPTED AGAINST ANY SET, because dropping zero changes nothing and the seam's
            // no-offset overload is exactly that call.
            records.Record(0, nothingDynamic, 0);
            Assert.True(records.IsDirty(0));
        }

        /// <summary>A flush that owes work with no pipeline bound is refused by name, because the index a
        /// resource lands at is a fact about the emission and there is no table to resolve it through. A flush
        /// that owes NOTHING is legal without one, which is what keeps a draw that binds no resource set at all
        /// legal.</summary>
        [Fact]
        public void AFlushWithWorkOwedAndNoPipelineIsRefusedAndAnEmptyOneIsNot()
        {
            using var harness = new MetalRingHarness();
            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            var sink = new FakeMetalEncoderSink(new FakeMetalEncoderCalls());

            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            records.Record(0, MetalBindProgram.Set(harness), 0);
            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => records.Flush(ref sink, Encoder, Epoch, segment: 0));

            Assert.Contains("no pipeline bound", thrown.Message, StringComparison.Ordinal);

            // AND THE MARK SURVIVES THE THROW, so the draw after the one that failed still owes its bind rather
            // than rendering against whatever the argument tables hold.
            Assert.True(records.IsDirty(0));
        }

        /// <summary>A nil encoder is refused rather than written into. A message to nil is a silent no-op in
        /// Objective-C, so the binds would go nowhere while the slots were marked clean, which is a frame
        /// rendering against stale argument tables with nothing reported.</summary>
        [Fact]
        public void ANilEncoderIsRefusedAndTheMarksSurvive()
        {
            using var harness = new MetalRingHarness();
            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            var sink = new FakeMetalEncoderSink(new FakeMetalEncoderCalls());

            records.SetIndexTable(MetalBindProgram.Table());
            records.Record(0, MetalBindProgram.Set(harness), 0);

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => records.Flush(ref sink, IntPtr.Zero, Epoch, segment: 0));

            Assert.Contains("orphan target", thrown.Message, StringComparison.Ordinal);
            Assert.True(records.IsDirty(0));
        }

        /// <summary>A slot past the cap is refused by name rather than sizing an array by itself, and a
        /// <c>Reset</c> puts the records back to what a fresh <c>MTLCommandBuffer</c> holds.</summary>
        [Fact]
        public void AWildSlotIsRefusedAndAResetForgetsEverything()
        {
            using var harness = new MetalRingHarness();
            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            MetalBoundSet set = MetalBindProgram.Set(harness);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => records.Record(MetalBindRecords.MaxSlot + 1, set, 0));

            records.SetIndexTable(MetalBindProgram.Table());
            records.Record(MetalBindRecords.MaxSlot, set, 0);
            Assert.Equal((int)MetalBindRecords.MaxSlot + 1, records.RecordedSlotCount);
            Assert.NotNull(records.IndexTable);

            records.Reset();

            Assert.Equal(0, records.RecordedSlotCount);
            Assert.Null(records.IndexTable);
            Assert.False(records.IsDirty(MetalBindRecords.MaxSlot));
        }

        /// <summary>The two arms are separate objects with separate stage sets, which is the seam's own split
        /// and Metal's too: a graphics bind reaches a render encoder's tables and a compute bind reaches a
        /// compute encoder's, and they are not the same tables.</summary>
        [Fact]
        public void TheGraphicsAndComputeArmsCarryTheirOwnStages()
        {
            Assert.Equal(
                new[] { MetalShaderStage.Vertex, MetalShaderStage.Fragment },
                MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment).Stages.ToArray());

            Assert.Equal(new[] { MetalShaderStage.Compute }, MetalBindRecords.ForCompute(MetalBindProgram.DeviceOffsetAlignment).Stages.ToArray());
        }

        /// <summary>
        /// THE OBLIGATION ROW 10 WROTE DOWN FOR THIS ROW, AS A FIELD WALK. A per-slot record holds a set's
        /// <c>Bindings</c> by reference, and the point of that being plain data is that resource CREATION does
        /// not enter the recorder's field graph: a record with a <see cref="MetalResourceSet"/> field would drag
        /// a layout and a liveness token in behind it, and one with a <see cref="MetalBuffer"/> field would drag
        /// the factory in behind that.
        /// <para>
        /// THIS IS V-D2's SHAPE AIMED AT A THIRD ANIMAL, and it is a field walk rather than an IL walk for V-D2's
        /// own reason: the question is whether a TYPE is reachable, which is exactly what a field graph answers.
        /// The positive control is at the bottom, because a walk that finds nothing proves nothing until it is
        /// shown to find something.
        /// </para>
        /// </summary>
        [Fact]
        public void TheBindRecords_ReachNoFactoryNoDeviceAndNoLayout()
        {
            var seen = new HashSet<Type>();
            Walk(typeof(MetalBindRecords), seen);
            Walk(typeof(MetalVertexStreamRecords), seen);

            Type[] forbidden =
            [
                typeof(MetalResourceFactory), typeof(MetalGpuDevice), typeof(MetalResourceLayout),
                typeof(MetalResourceSet), typeof(IMetalDeviceLiveness),
            ];

            foreach (Type type in forbidden)
            {
                Assert.False(seen.Contains(type),
                    $"The native Metal bind records reach {type.Name} through their field graph. A per-slot "
                    + "record holds a set's resolved bindings as plain data precisely so resource creation stays "
                    + "out of the recorder, which is the obligation row 10 wrote down for this row. Hold "
                    + "MetalBoundSet rather than the set.");
            }

            // THE POSITIVE CONTROL. The walk must reach the things the records genuinely do hold, or it is
            // finding nothing because it is broken rather than because nothing is there.
            Assert.Contains(typeof(MetalBoundSet), seen);
            Assert.Contains(typeof(MetalBoundResource), seen);
            Assert.Contains(typeof(MetalShaderIndexTable), seen);
            Assert.Contains(typeof(MetalArgumentBatch), seen);
        }

        static void Walk(Type type, HashSet<Type> seen)
        {
            while (type.IsArray) type = type.GetElementType()!;

            if (type.IsPrimitive || type == typeof(string) || !seen.Add(type)) return;

            const System.Reflection.BindingFlags All =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

            foreach (System.Reflection.FieldInfo field in type.GetFields(All)) Walk(field.FieldType, seen);
            foreach (Type nested in type.GetNestedTypes(All)) Walk(nested, seen);
        }

        // A FABRICATED ENCODER AND A FIXED EPOCH, for the tests whose subject is the state machine rather than
        // the invalidation. The tests that ARE about the invalidation drive a real MetalEncoderScope, because an
        // epoch a test chose is an epoch a test can be wrong about in the same direction as the code.
        static readonly IntPtr Encoder = new(0x4D544C45);

        const ulong Epoch = 7;
    }
}
