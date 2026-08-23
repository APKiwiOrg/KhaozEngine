using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// WHAT THE BIND FLUSH ACTUALLY EMITS, DEVICE-FREE: which calls, at which indices, with which handles and
    /// which offsets. Decisions M-R6, M-R7 and M-M4 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>, work-breakdown row 13
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/579).
    ///
    /// <para>
    /// IT BINDS THROUGH A REAL INDEX TABLE READ OUT OF A REAL MSL EMISSION (2.2b), which is the whole reason
    /// these can be device-free and still mean something. <c>MetalShaderBuild</c> is the device-free half of the
    /// shader path, so the table here is built by the shipped mechanism from the shipped cross-compiler, and the
    /// partial-stage case the flush has to get right (a stage with no entry for an element) arrives from the
    /// emission rather than from a fixture that decided it.
    /// </para>
    /// <para>
    /// THE FAILURE THIS CATCHES IS A WRONG PIXEL WITH NO ERROR ATTACHED. Metal reports nothing when a resource is
    /// bound at the wrong index, when a stage is bound something it does not read, or when a composed offset
    /// walks into the next frame's segment: the first two are the class that produced 7.25.0, 7.51.2 and the
    /// splat terrain, and the third is silent by construction because <c>setBufferOffset:</c> carries no length.
    /// </para>
    /// </summary>
    public sealed class MetalBindFlushTests
    {
        readonly ITestOutputHelper _output;

        public MetalBindFlushTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// M-R6, WHICH IS THE ROW'S HEADLINE. A full activation is ONE ARRAY CALL PER (kind, stage), not one call
        /// per resource per stage. The incumbent emits the latter, which is the #418 fan-out defect arriving on a
        /// second API, and the vendored fork's binding layer does not declare a single array setter, so this is
        /// the shape the design says had to be written rather than copied.
        /// </summary>
        [Fact]
        public void AFullActivationIsOneArrayCallPerKindPerStage()
        {
            using var harness = new MetalRingHarness();
            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            records.SetIndexTable(MetalBindProgram.Table());
            records.Record(0, MetalBindProgram.Set(harness), 0);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            foreach (FakeMetalArrayWrite write in calls.ArrayWrites)
            {
                _output.WriteLine($"{write.Stage} {write.Space} [{write.FirstIndex}..{write.EndIndex - 1}]");
            }

            // The shape section 6.3 names: one buffer call on the vertex stage, and one buffer, one texture and
            // one sampler call on the fragment stage.
            Assert.Equal(1, Writes(calls, MetalShaderStage.Vertex, MetalIndexSpace.Buffer));
            Assert.Equal(0, Writes(calls, MetalShaderStage.Vertex, MetalIndexSpace.Texture));
            Assert.Equal(0, Writes(calls, MetalShaderStage.Vertex, MetalIndexSpace.Sampler));
            Assert.Equal(1, Writes(calls, MetalShaderStage.Fragment, MetalIndexSpace.Buffer));
            Assert.Equal(1, Writes(calls, MetalShaderStage.Fragment, MetalIndexSpace.Texture));
            Assert.Equal(1, Writes(calls, MetalShaderStage.Fragment, MetalIndexSpace.Sampler));

            Assert.Equal(4, calls.ArgumentTableWrites);
            Assert.Empty(calls.OffsetWrites);
        }

        /// <summary>
        /// 2.2b's RULE, AND THE ONE MOST EASILY GOT WRONG: an element with no entry for a stage is NOT bound for
        /// that stage. It is not a miss to work around and not a zero to fall back to, because the cross-compiler
        /// omits an argument a stage does not reference, so the element genuinely is not in that stage's
        /// signature. Binding one anyway is what an index-counting backend does that produces the off-by-one.
        /// <para>
        /// THE FIXTURE'S VERTEX STAGE READS BINDING 0 AND NOTHING ELSE, so the vertex buffer run must cover
        /// exactly ONE index, and the texture and sampler must reach the fragment stage alone. Over the shipped
        /// corpus 95 of 254 stage/element slots are unreferenced, so this is the common case rather than the
        /// corner.
        /// </para>
        /// </summary>
        [Fact]
        public void AStageWithNoEntryForAnElementIsNotBoundForIt()
        {
            using var harness = new MetalRingHarness();
            MetalShaderIndexTable table = MetalBindProgram.Table();

            // The premise, read off the table itself rather than assumed: the vertex stage really does reference
            // only the frame UBO, and the fragment stage really does reference all four.
            Assert.True(table.TryGetIndex(0, MetalBindProgram.FrameBinding, MetalShaderStage.Vertex, out _));
            Assert.False(table.TryGetIndex(0, MetalBindProgram.MaterialBinding, MetalShaderStage.Vertex, out _));
            Assert.False(table.TryGetIndex(0, MetalBindProgram.TextureBinding, MetalShaderStage.Vertex, out _));
            Assert.False(table.TryGetIndex(0, MetalBindProgram.SamplerBinding, MetalShaderStage.Vertex, out _));
            Assert.True(table.TryGetIndex(0, MetalBindProgram.TextureBinding, MetalShaderStage.Fragment, out _));

            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            records.SetIndexTable(table);
            records.Record(0, MetalBindProgram.Set(harness), 0);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            FakeMetalArrayWrite vertexBuffers = Single(calls, MetalShaderStage.Vertex, MetalIndexSpace.Buffer);
            Assert.Single(vertexBuffers.Objects);

            table.TryGetIndex(0, MetalBindProgram.FrameBinding, MetalShaderStage.Vertex,
                out MetalIndexTableEntry frame);
            Assert.Equal((uint)frame.Index, vertexBuffers.FirstIndex);

            // AND THE FRAGMENT STAGE GETS BOTH BUFFERS, which is what makes the assertion above a statement about
            // visibility rather than about the set being short.
            Assert.Equal(2, Single(calls, MetalShaderStage.Fragment, MetalIndexSpace.Buffer).Objects.Length);
        }

        /// <summary>
        /// M-R7. A slot whose ONLY change is its dynamic offset emits one <c>setBufferOffset:</c> per VISIBLE
        /// stage and no argument-table write at all. This is the shadow pass's shape thousands of times a frame,
        /// and it is a different SELECTOR rather than a cheaper variant of the array setter: it writes an integer
        /// into the encoder's command stream where <c>setBuffers:</c> writes whole argument-table entries.
        /// <para>
        /// ONE CALL PER VISIBLE STAGE AND NOT ONE PER BINDING. The set has four elements and only the frame UBO
        /// is declared dynamic, so the other three composed offsets did not move and re-emitting them would be
        /// three calls bought for nothing.
        /// </para>
        /// </summary>
        [Fact]
        public void OnlyTheDynamicOffsetMovingIsOneOffsetCallPerVisibleStage()
        {
            using var harness = new MetalRingHarness();
            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            MetalBoundSet set = MetalBindProgram.Set(harness);
            MetalShaderIndexTable table = MetalBindProgram.Table();

            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            records.SetIndexTable(table);
            records.Record(0, set, 0);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            int afterActivation = calls.ArgumentTableWrites;

            records.Record(0, set, 128);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            Assert.Equal(afterActivation + 2, calls.ArgumentTableWrites);
            Assert.Equal(2, calls.OffsetWrites.Count);
            Assert.Equal(
                new[] { MetalShaderStage.Vertex, MetalShaderStage.Fragment },
                calls.OffsetWrites.Select(o => o.Stage).ToArray());

            // AT THE STAGE'S OWN INDEX, which is the point of the table: the same element is a different index in
            // the two stages more often than not (80 of 159 emitted arguments over the shipped set carry an index
            // that differs from their binding number alone).
            foreach (FakeMetalOffsetWrite write in calls.OffsetWrites)
            {
                Assert.True(table.TryGetIndex(0, MetalBindProgram.FrameBinding, write.Stage,
                    out MetalIndexTableEntry entry));
                Assert.Equal((uint)entry.Index, write.Index);
                Assert.Equal((nuint)128, write.Offset);
            }
        }

        /// <summary>
        /// M-R4, WRITTEN BEHAVIOURALLY AND AIMED AT THE CORRUPTION RATHER THAN THE BOOKKEEPING. A record-time
        /// <c>UpdateBuffer</c> big enough to take the staging path opens a blit encoder MID-PASS, so the next
        /// <c>PrepareDraw</c> reopens the pass on a NEW encoder whose argument table is empty. The sharp case is
        /// exactly this one: the only thing that changed is the dynamic offset, which would ordinarily take the
        /// offsets-only arm, and <c>setBufferOffset:</c> against an index holding no buffer at all is undefined.
        /// So the boundary has to beat the offsets-only comparison, and here it does.
        /// </summary>
        [Fact]
        public void AnEncoderBoundaryForcesAFullRebindAndNotAnOffsetsOnlyCall()
        {
            using var harness = new MetalRingHarness();
            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            MetalBoundSet set = MetalBindProgram.Set(harness);

            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);
            var scope = new MetalEncoderScope(sink);
            scope.BeginRecording(new IntPtr(0x100));

            records.SetIndexTable(MetalBindProgram.Table());
            records.Record(0, set, 0);

            IntPtr first = scope.EnsureRenderEncoder(Descriptor);
            records.Flush(ref sink, first, scope.Epoch, segment: 0);

            int afterActivation = calls.ArgumentTableWrites;
            Assert.True(records.IsEmittedIn(0, scope.Epoch));

            // THE BLIT IS THE BOUNDARY, and the reopen is a different encoder with an empty argument table.
            scope.EnsureBlitEncoder();
            IntPtr second = scope.EnsureRenderEncoder(Descriptor);
            Assert.NotEqual(first, second);
            Assert.False(records.IsEmittedIn(0, scope.Epoch));

            records.Record(0, set, 128);
            records.Flush(ref sink, second, scope.Epoch, segment: 0);

            Assert.Empty(calls.OffsetWrites);
            Assert.Equal(afterActivation * 2, calls.ArgumentTableWrites);
            Assert.All(calls.ArrayWrites.Skip(afterActivation), w => Assert.Equal(second, w.Encoder));
        }

        /// <summary>
        /// THE COMPOSED OFFSET (M-M4): <c>frameBase + rangeOffset + callerDynamicOffset</c>, where the frame base
        /// is the segment THIS RECORDING captured at its <c>Begin</c> and never the allocator's live one. Shipped
        /// paths open several lists per frame, so a bind composed against the allocator's current segment would
        /// name a version this recording never wrote.
        /// <para>
        /// AND THE CALLER'S OFFSET REACHES ONLY THE DECLARED-DYNAMIC ELEMENT, which is the one thing
        /// <c>GpuResourceLayoutElement.Dynamic</c> decides on this backend.
        /// </para>
        /// <para>
        /// THIS IS ALSO THE ALIGNMENT CHECK'S POSITIVE CONTROL. <c>base + 16 + 128</c> is a multiple of the
        /// 16-byte alignment the fixture stands the device up with, so it composes and emits, and
        /// <see cref="AComposedOffsetThatIsNotAMultipleOfTheDeviceAlignmentIsRefused"/> is the same shape with
        /// the caller's offset moved off a multiple.
        /// </para>
        /// </summary>
        [Fact]
        public void TheComposedOffsetIsTheRecordingsSegmentBasePlusTheRangeAndTheCallerOffset()
        {
            using var harness = new MetalRingHarness();
            MetalBuffer frame = harness.NewBuffer(64, GpuBufferUsage.UniformBuffer);
            MetalBuffer material = harness.NewBuffer(32, GpuBufferUsage.UniformBuffer);

            MetalBoundSet set = MetalBindProgram.Set(
                new MetalBoundResource(MetalIndexSpace.Buffer, frame, RangeOffset: 16, Range: 48,
                    AppliesCallerOffset: true),
                new MetalBoundResource(MetalIndexSpace.Buffer, material, RangeOffset: 0, Range: 32,
                    AppliesCallerOffset: false),
                new MetalBoundResource(MetalIndexSpace.Texture, new FakeMetalBindable(0x7E11), 0, 0, false),
                new MetalBoundResource(MetalIndexSpace.Sampler, new FakeMetalBindable(0x5A11), 0, 0, false));

            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            MetalShaderIndexTable table = MetalBindProgram.Table();
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            records.SetIndexTable(table);
            records.Record(0, set, 128);
            records.Flush(ref sink, Encoder, Epoch, segment: 1);

            table.TryGetIndex(0, MetalBindProgram.FrameBinding, MetalShaderStage.Fragment,
                out MetalIndexTableEntry frameEntry);
            table.TryGetIndex(0, MetalBindProgram.MaterialBinding, MetalShaderStage.Fragment,
                out MetalIndexTableEntry materialEntry);

            FakeMetalArrayWrite buffers = Single(calls, MetalShaderStage.Fragment, MetalIndexSpace.Buffer);

            // The dynamic one: segment 1's base, plus the set's own range offset, plus the caller's per-draw
            // offset. The other one: the same base and its own range offset, and NOT the caller's.
            Assert.Equal((nuint)(frame.Ring!.SegmentBaseBytes(1) + 16 + 128),
                buffers.Offsets[frameEntry.Index - (int)buffers.FirstIndex]);
            Assert.Equal((nuint)material.Ring!.SegmentBaseBytes(1),
                buffers.Offsets[materialEntry.Index - (int)buffers.FirstIndex]);

            // AND THE OTHER SEGMENT IS A DIFFERENT ANSWER, which is what makes the first assertion a statement
            // about the segment rather than an arithmetic coincidence at zero.
            Assert.NotEqual(0UL, frame.Ring!.SegmentBaseBytes(1));
        }

        /// <summary>
        /// M-M4's REFUSAL, AND IT IS GENUINELY THIS PATH'S. The set's own creation-time call passes a caller
        /// offset of zero and cannot fire: the window check there already bounds <c>rangeOffset + range</c> by the
        /// logical size and the stride is that size rounded up. Here the caller's real per-draw offset is in hand.
        /// <para>
        /// NOTHING WOULD REPORT IT OTHERWISE. <c>setBufferOffset:atIndex:</c> takes an offset and no length, so
        /// there is no descriptor range to overrun, no VUID and no validation layer to trip: the shader would read
        /// the NEXT frame's uniforms on the frame slots where there is a next one, and past the buffer entirely on
        /// the last.
        /// </para>
        /// </summary>
        [Fact]
        public void ABindWindowThatLeavesItsOwnSegmentIsRefusedAndTheMarksSurvive()
        {
            using var harness = new MetalRingHarness();
            MetalBuffer buffer = harness.NewBuffer(256, GpuBufferUsage.UniformBuffer);

            // A range equal to the stride is the shape that looks safe, and it fits only while the caller's own
            // offset is zero. It is non-zero in five shipped renderers.
            MetalBoundSet set = MetalBindProgram.Set(
                new MetalBoundResource(MetalIndexSpace.Buffer, buffer, 0, 256, AppliesCallerOffset: true));

            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            records.SetIndexTable(MetalBindProgram.Table());
            records.Record(0, set, 0);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);
            Assert.NotEmpty(calls.ArrayWrites);

            records.Record(0, set, 256);
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(
                () => records.Flush(ref sink, Encoder, Epoch, segment: 0));

            Assert.Contains("runs past the end of its own", thrown.Message, StringComparison.Ordinal);

            // THE MARK SURVIVES, which is what turns a loud repeatable refusal into a loud repeatable refusal
            // rather than one throw followed by a frame of silence.
            Assert.True(records.IsDirty(0));
        }

        /// <summary>
        /// A RESOURCE DISPOSED SINCE ITS SET WAS CREATED DEGRADES TO AN UNBOUND INDEX, by construction rather than
        /// by a rule nobody can enforce. The binding holds the wrapper and reads its handle and its ring THROUGH
        /// that wrapper's own disposal guard at the bind, so a nil goes into the argument table, which Metal reads
        /// as an unbound index. The alternative a snapshot would produce is the released pointer written into the
        /// table silently.
        /// </summary>
        [Fact]
        public void AResourceDisposedAfterItsSetWasBuiltBindsNilRatherThanAReleasedPointer()
        {
            using var harness = new MetalRingHarness();
            MetalBuffer frame = harness.NewBuffer(64, GpuBufferUsage.UniformBuffer);

            MetalBoundSet set = MetalBindProgram.Set(
                new MetalBoundResource(MetalIndexSpace.Buffer, frame, 0, 64, AppliesCallerOffset: true));

            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            MetalShaderIndexTable table = MetalBindProgram.Table();
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            harness.DisposeWithoutRelease(frame);
            Assert.Null(frame.Ring);

            records.SetIndexTable(table);
            records.Record(0, set, 128);
            records.Flush(ref sink, Encoder, Epoch, segment: 1);

            FakeMetalArrayWrite vertex = Single(calls, MetalShaderStage.Vertex, MetalIndexSpace.Buffer);
            Assert.Equal(IntPtr.Zero, vertex.Objects[0]);

            // AND THE UNRINGED ARM COMPOSES WITHOUT A FRAME BASE, which is the row 8 predicate live at the bind:
            // no ring means no segment to add, so what is left is the range offset and the caller's.
            Assert.Equal((nuint)128, vertex.Offsets[0]);
        }

        /// <summary>
        /// SECTION 18's THIRD NAMED RISK FOR THIS ROW, WHICH NOTHING ENFORCED: an unaligned buffer offset is a
        /// validation error under the debug layer and undefined behaviour without it, and only ONE of the three
        /// terms is aligned by construction. The ring segment base is a multiple of M-M3's 256-byte stride, while
        /// the set's own range offset and the caller's per-draw offset are raw values arriving through the seam.
        /// <para>
        /// AND THE NUMBER IS THE DEVICE'S, WHICH IS THE WHOLE POINT OF THREADING IT DOWN. macOS reports 16 or 32
        /// through <c>minimumConstantBufferOffsetAlignment</c>, so checking against the 256-byte stride would
        /// refuse binds every shipped device accepts, and picking a small constant here would pass binds a device
        /// does not. The value reaches the records from <c>MetalDeviceFacts</c> by way of the command list, and a
        /// device-free test stands it up at a fixed 16.
        /// </para>
        /// </summary>
        [Fact]
        public void AComposedOffsetThatIsNotAMultipleOfTheDeviceAlignmentIsRefused()
        {
            using var harness = new MetalRingHarness();
            MetalBuffer frame = harness.NewBuffer(64, GpuBufferUsage.UniformBuffer);

            MetalBoundSet set = MetalBindProgram.Set(
                new MetalBoundResource(MetalIndexSpace.Buffer, frame, 0, 64, AppliesCallerOffset: true));

            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            Assert.Equal(MetalBindProgram.DeviceOffsetAlignment, records.OffsetAlignment);

            records.SetIndexTable(MetalBindProgram.Table());

            // THE POSITIVE CONTROL FIRST, in the same shape: an offset ON a multiple composes and emits.
            records.Record(0, set, MetalBindProgram.DeviceOffsetAlignment);
            records.Flush(ref sink, Encoder, Epoch, segment: 1);
            Assert.NotEmpty(calls.ArrayWrites);

            // HALF THE ALIGNMENT, which is inside the window M-M4 bounds (0 + 8 + 64 against a 256-byte segment)
            // and therefore reaches the alignment check rather than the window one.
            records.Record(0, set, MetalBindProgram.DeviceOffsetAlignment / 2);

            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(
                () => records.Flush(ref sink, Encoder, Epoch, segment: 1));

            // ALL THREE COMPONENTS ARE NAMED, because the composed number on its own does not say which of them
            // to go and look at.
            Assert.Contains("ring segment base", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("range offset", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("per-draw offset", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("16-byte buffer-offset alignment", thrown.Message, StringComparison.Ordinal);

            // AND THE REFUSAL IS A WHOLE-FLUSH REFUSAL like every other one on this path.
            Assert.True(records.IsDirty(0));
            Assert.Equal(0, records.StagedEntries);
        }

        /// <summary>
        /// THE OFFSETS-ONLY ARM DOES NOT SURVIVE A DISPOSAL, which is the one shape the array-reference compare
        /// cannot see. A binding holds the WRAPPER and reads its handle and its ring through the wrapper's own
        /// disposal guard at every bind, so a resource released between two draws changes both values while the
        /// bindings ARRAY stays the identical object. A slot rebound with a moved dynamic offset therefore looks
        /// exactly like the shadow pass's own shape, and taking the derived arm there would compose an offset off
        /// the unringed branch (no frame base, no window check) and send <c>setBufferOffset:</c> to a table index
        /// still holding the released buffer, which Metal accepts without a word.
        /// <para>
        /// SO THE FALLBACK IS THE FULL ARM AND THE FULL ARM'S NIL IS THE DEGRADATION row 10's correction
        /// promised. What the driver ends up with is an unbound index rather than a moved window on a freed
        /// allocation.
        /// </para>
        /// </summary>
        [Fact]
        public void ARingBackedBufferDisposedBetweenTwoDrawsFallsBackToTheFullArm()
        {
            using var harness = new MetalRingHarness();
            MetalBuffer frame = harness.NewBuffer(64, GpuBufferUsage.UniformBuffer);
            MetalBuffer material = harness.NewBuffer(32, GpuBufferUsage.UniformBuffer);

            MetalBoundSet set = MetalBindProgram.Set(
                new MetalBoundResource(MetalIndexSpace.Buffer, frame, 0, 64, AppliesCallerOffset: true),
                new MetalBoundResource(MetalIndexSpace.Buffer, material, 0, 32, AppliesCallerOffset: false),
                new MetalBoundResource(MetalIndexSpace.Texture, new FakeMetalBindable(0x7E11), 0, 0, false),
                new MetalBoundResource(MetalIndexSpace.Sampler, new FakeMetalBindable(0x5A11), 0, 0, false));

            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            MetalShaderIndexTable table = MetalBindProgram.Table();
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            records.SetIndexTable(table);
            records.Record(0, set, 0);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            Assert.NotNull(frame.Ring);
            int arraysAfterActivation = calls.ArrayWrites.Count;

            // THE DISPOSAL IS THE ONLY THING THAT HAPPENS BETWEEN THE TWO DRAWS. The set is the same object and
            // the recorded array is the same array.
            harness.DisposeWithoutRelease(frame);
            Assert.Null(frame.Ring);

            records.Record(0, set, 128);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            // NEVER A BARE setBufferOffset:, and a full re-activation instead.
            Assert.Empty(calls.OffsetWrites);
            Assert.Equal(arraysAfterActivation + 4, calls.ArrayWrites.Count);

            // AND THE SINK SAW A NIL, which is the unbound index the degradation is.
            FakeMetalArrayWrite vertex = calls.ArrayWrites
                .Last(w => w.Stage == MetalShaderStage.Vertex && w.Space == MetalIndexSpace.Buffer);

            Assert.Equal(IntPtr.Zero, Assert.Single(vertex.Objects));

            FakeMetalArrayWrite fragment = calls.ArrayWrites
                .Last(w => w.Stage == MetalShaderStage.Fragment && w.Space == MetalIndexSpace.Buffer);

            Assert.True(table.TryGetIndex(0, MetalBindProgram.FrameBinding, MetalShaderStage.Fragment,
                out MetalIndexTableEntry frameEntry));
            Assert.Equal(IntPtr.Zero, fragment.Objects[frameEntry.Index - (int)fragment.FirstIndex]);
        }

        /// <summary>
        /// A NIL IN THE MIDDLE OF A RUN DOES NOT SPLIT IT, which is the deliberate half of the disposal
        /// degradation and the half nothing exercised. A HOLE cuts a run because the index is not being written
        /// at all and a nil there would unbind whatever another slot legitimately put in it. A NIL HANDLE is the
        /// opposite case: the index IS being written, and what it is written with is nil, which Metal reads as an
        /// unbound index. So the run stays whole and the middle entry carries a nil.
        /// <para>
        /// THREE BUFFERS ARE THE MINIMUM THAT CAN TELL THOSE APART. In a two-element run the nil is always at an
        /// end, where "the run did not split" and "the run stopped early" produce the same call log.
        /// </para>
        /// <para>
        /// AND WHICH ONE IS IN THE MIDDLE IS READ OFF THE TABLE rather than assumed. The two sets' three buffers
        /// land at 0, 1 and 2 in both stages, and since 18.0.0 the order is the authored one, ascending
        /// <c>(set, binding)</c>, so slot 0's second buffer is the middle. Reading it means a renumbering fails
        /// on the premise below instead of on the subject.
        /// </para>
        /// </summary>
        [Fact]
        public void ANilHandleBetweenTwoLiveOnesDoesNotSplitTheRun()
        {
            using var harness = new MetalRingHarness();
            MetalShaderIndexTable table = MetalTwoSetProgram.Table();

            MetalBuffer frame = harness.NewBuffer(64, GpuBufferUsage.UniformBuffer);
            MetalBuffer material = harness.NewBuffer(32, GpuBufferUsage.UniformBuffer);
            MetalBuffer model = harness.NewBuffer(32, GpuBufferUsage.UniformBuffer);

            MetalBoundSet slotZero = MetalBindProgram.Set(
                new MetalBoundResource(MetalIndexSpace.Buffer, frame, 0, 64, AppliesCallerOffset: true),
                new MetalBoundResource(MetalIndexSpace.Buffer, material, 0, 32, AppliesCallerOffset: false));

            MetalBoundSet slotOne = MetalBindProgram.Set(
                new MetalBoundResource(MetalIndexSpace.Buffer, model, 0, 32, AppliesCallerOffset: false));

            // THE PREMISE, READ OFF THE TABLE: three contiguous indices with slot 0's second buffer in the
            // middle.
            Assert.True(table.TryGetIndex(0, MetalTwoSetProgram.MaterialBinding, MetalShaderStage.Fragment,
                out MetalIndexTableEntry middle));
            Assert.True(table.TryGetIndex(0, MetalTwoSetProgram.FrameBinding, MetalShaderStage.Fragment,
                out MetalIndexTableEntry frameEntry));
            Assert.True(table.TryGetIndex(1, MetalTwoSetProgram.ObjectBinding, MetalShaderStage.Fragment,
                out MetalIndexTableEntry objectEntry));
            Assert.Equal(
                new[] { middle.Index - 1, middle.Index, middle.Index + 1 },
                new[] { frameEntry.Index, middle.Index, objectEntry.Index });

            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            harness.DisposeWithoutRelease(material);
            Assert.Equal(IntPtr.Zero, ((IMetalBindable)material).BindHandle);

            records.SetIndexTable(table);
            records.Record(0, slotZero, 0);
            records.Record(1, slotOne, 0);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            FakeMetalArrayWrite buffers = Single(calls, MetalShaderStage.Fragment, MetalIndexSpace.Buffer);

            // ONE CALL, THREE INDICES, and the middle pointer nil with both neighbours live.
            Assert.Equal(3, buffers.Objects.Length);

            int at = middle.Index - (int)buffers.FirstIndex;
            Assert.Equal(IntPtr.Zero, buffers.Objects[at]);
            Assert.NotEqual(IntPtr.Zero, buffers.Objects[at - 1]);
            Assert.NotEqual(IntPtr.Zero, buffers.Objects[at + 1]);
        }

        /// <summary>
        /// DOOR ONE OF THE PARTIAL FLUSH: a throw inside the slot walk happens with entries already STAGED, and
        /// they are staged in the batch rather than emitted, so <see cref="MetalArgumentBatch.Emit"/>'s own
        /// clearing <c>finally</c> is never reached. Left there, they belong to the NEXT flush, which emits them
        /// into whichever stage it reaches first: a bind of one stage's resources into another stage's table when
        /// the indices do not collide, and a duplicate-index refusal blaming a table disagreement that never
        /// happened when they do.
        /// <para>
        /// SLOT 1 IS THE ONE THAT THROWS, so slot 0's entries are in the batch when it does. That ordering is the
        /// whole point of the row: a single-slot flush cannot tell "the batch was dropped" from "nothing was ever
        /// staged".
        /// </para>
        /// </summary>
        [Fact]
        public void AFlushRefusedPartWayThroughStagesNothingIntoTheNextOne()
        {
            using var harness = new MetalRingHarness();
            MetalShaderIndexTable table = MetalTwoSetProgram.Table();

            MetalBuffer frame = harness.NewBuffer(64, GpuBufferUsage.UniformBuffer);
            MetalBuffer material = harness.NewBuffer(32, GpuBufferUsage.UniformBuffer);
            MetalBuffer model = harness.NewBuffer(32, GpuBufferUsage.UniformBuffer);

            // Set() here is just the MetalBoundSet factory, and these are the TWO-SET program's sets.
            MetalBoundSet slotZero = MetalBindProgram.Set(
                new MetalBoundResource(MetalIndexSpace.Buffer, frame, 0, 64, AppliesCallerOffset: true),
                new MetalBoundResource(MetalIndexSpace.Buffer, material, 0, 32, AppliesCallerOffset: false));

            // 512 BYTES OUT OF A 256-BYTE SEGMENT, which M-M4 refuses, and it is slot 1 so the refusal lands
            // after slot 0's two entries are staged.
            MetalBoundSet slotOneRefused = MetalBindProgram.Set(
                new MetalBoundResource(MetalIndexSpace.Buffer, model, 0, 512, AppliesCallerOffset: false));

            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            records.SetIndexTable(table);
            records.Record(0, slotZero, 0);
            records.Record(1, slotOneRefused, 0);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => records.Flush(ref sink, Encoder, Epoch, segment: 0));

            Assert.Equal(0, records.StagedEntries);

            int afterRefusal = calls.ArrayWrites.Count;

            MetalBoundSet slotOneFits = MetalBindProgram.Set(
                new MetalBoundResource(MetalIndexSpace.Buffer, model, 0, 32, AppliesCallerOffset: false));

            records.Record(1, slotOneFits, 0);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            // AND THE NEXT FLUSH EMITS ONLY WHAT IT STAGED: three buffers per stage and nothing else. Counted as
            // objects rather than as calls, because how the three indices cut into runs is the emission's
            // business and this row is about the leftovers.
            Assert.Equal(6, calls.ArrayWrites.Skip(afterRefusal).Sum(w => w.Objects.Length));
        }

        /// <summary>
        /// DOOR TWO: a throw AFTER an earlier stage emitted never reaches the trailing record-update loop, so
        /// <c>EmittedBindings</c> still names the OLD set while the encoder's table holds the NEW one's
        /// resources. The next bind of the old set then derives the offsets-only arm and moves an offset on a
        /// table entry holding a different buffer, which renders wrong with nothing reported: Metal's
        /// <c>setBufferOffset:</c> takes an index and an offset and asks no questions about what is at that
        /// index.
        /// <para>
        /// THE SEQUENCE IS BIND A, DRAW, BIND B, DRAW-THAT-THROWS, BIND A, DRAW. B's vertex stage emits cleanly
        /// because the fixture's vertex function reads only the frame UBO, and its fragment stage is where the
        /// refused window is, so the throw genuinely lands with one stage already written.
        /// </para>
        /// </summary>
        [Fact]
        public void ASetReboundAfterAFlushThrewMidWayTakesTheFullArmAndNotTheOffsetsOnlyOne()
        {
            using var harness = new MetalRingHarness();
            MetalBoundSet setA = MetalBindProgram.Set(harness);

            MetalBuffer frame = harness.NewBuffer(64, GpuBufferUsage.UniformBuffer);
            MetalBuffer material = harness.NewBuffer(32, GpuBufferUsage.UniformBuffer);

            // B's MATERIAL BINDING IS THE REFUSED ONE, and the fixture's material is read by the FRAGMENT stage
            // alone, so the vertex stage lands its array call before the fragment stage throws.
            MetalBoundSet setB = MetalBindProgram.Set(
                new MetalBoundResource(MetalIndexSpace.Buffer, frame, 0, 64, AppliesCallerOffset: true),
                new MetalBoundResource(MetalIndexSpace.Buffer, material, 0, 512, AppliesCallerOffset: false),
                new MetalBoundResource(MetalIndexSpace.Texture, new FakeMetalBindable(0x7E11), 0, 0, false),
                new MetalBoundResource(MetalIndexSpace.Sampler, new FakeMetalBindable(0x5A11), 0, 0, false));

            var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            records.SetIndexTable(MetalBindProgram.Table());
            records.Record(0, setA, 0);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            records.Record(0, setB, 0);
            Assert.Throws<ArgumentOutOfRangeException>(
                () => records.Flush(ref sink, Encoder, Epoch, segment: 0));

            // THE VERTEX STAGE REALLY DID LAND, which is what makes this row about a stale record rather than
            // about a flush that changed nothing.
            Assert.Equal(2, Writes(calls, MetalShaderStage.Vertex, MetalIndexSpace.Buffer));
            Assert.False(records.IsEmittedIn(0, Epoch));

            int arraysBefore = calls.ArrayWrites.Count;

            records.Record(0, setA, 128);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            // A FULL REBIND, and emphatically not two setBufferOffset: calls against a table holding B.
            Assert.Empty(calls.OffsetWrites);
            Assert.Equal(arraysBefore + 4, calls.ArrayWrites.Count);
        }

        static int Writes(FakeMetalEncoderCalls calls, MetalShaderStage stage, MetalIndexSpace space)
            => calls.ArrayWrites.Count(w => w.Stage == stage && w.Space == space);

        static FakeMetalArrayWrite Single(FakeMetalEncoderCalls calls, MetalShaderStage stage,
            MetalIndexSpace space)
            => calls.ArrayWrites.Single(w => w.Stage == stage && w.Space == space);

        static readonly IntPtr Encoder = new(0x4D544C45);
        static readonly IntPtr Descriptor = new(0x4D544C44);

        const ulong Epoch = 7;
    }
}
