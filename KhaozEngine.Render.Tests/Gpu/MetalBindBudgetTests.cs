using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION M-T2's BUDGET, FROZEN, DEVICE-FREE, over the real shipped shader catalog. The per-draw native
    /// call marginals of the native Metal bind path, counted through <see cref="IMetalEncoderSink"/>, which is
    /// the seam that exists so a number can be asserted about an EMISSION rather than about what a recorder
    /// reports. Work-breakdown row 13 (https://github.com/APKiwiOrg/KhaozEngine/issues/579).
    ///
    /// <para><b>THESE ARE REGRESSION TARGETS AND NOT PARITY TARGETS.</b> The incumbent emits one call per
    /// resource per stage and re-binds every vertex stream on every draw unconditionally, so every number here is
    /// strictly lower than what the Veldrid Metal leg pays. Freezing the lower number is what makes a future
    /// change that reintroduces the fan-out a red test rather than an invisible cost, which is the whole reason
    /// the seam is shaped around call CLASSES rather than around a recorder's own bookkeeping.</para>
    ///
    /// <para><b>THE CLASSES ARE METAL'S AND NOT EITHER NEIGHBOUR'S.</b> Direct3D 11's fan-out class is one call
    /// per resource per stage. Vulkan's is per-draw descriptor set allocation, and Metal allocates no descriptor
    /// of any kind. Metal's is argument-table writes AND an encoder boundary per record-time upload, and the
    /// second has no analogue anywhere else in the program: a budget ported from either predecessor would pass
    /// green while a record-time <c>UpdateBuffer</c> split the encoder a thousand times a frame.</para>
    ///
    /// <para><b>THE CATALOG ROWS ARE A MEASUREMENT AND THE MODEL ROWS ARE A FREEZE.</b> How many calls a full
    /// activation takes is a property of what each program's emission REFERENCES, so it is reported per program
    /// and bounded rather than pinned at one number. What is pinned is the shape section 6.3 states.</para>
    /// </summary>
    public sealed class MetalBindBudgetTests
    {
        readonly ITestOutputHelper _output;

        public MetalBindBudgetTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// THE NUMBER SECTION 6.3 NAMES, FROZEN: a full activation of the engine's model-shaped set is ONE buffer
        /// call, ONE texture call and ONE sampler call on the fragment stage plus ONE buffer call on the vertex
        /// stage. Four native calls where the incumbent's per-element shape pays one per element per referencing
        /// stage, which for this set is five.
        /// </summary>
        [Fact]
        public void AFullActivationOfTheModelShapedSetIsFourNativeCalls()
        {
            using var harness = new MetalRingHarness();
            var records = MetalBindRecords.ForGraphics();
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            records.SetIndexTable(MetalBindProgram.Table());
            records.Record(0, MetalBindProgram.Set(harness), 0);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            Assert.Equal(4, calls.ArgumentTableWrites);
        }

        /// <summary>
        /// THE STEADY STATE IS ZERO. A draw that follows a draw with nothing rebound emits no argument-table
        /// write at all, which is what makes clause 7's collapse worth having: a renderer that re-binds its sets
        /// every draw pays for the first one and nothing after it.
        /// </summary>
        [Fact]
        public void ADrawThatChangesNothingIsZeroNativeCalls()
        {
            using var harness = new MetalRingHarness();
            var records = MetalBindRecords.ForGraphics();
            MetalBoundSet set = MetalBindProgram.Set(harness);
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            records.SetIndexTable(MetalBindProgram.Table());
            records.Record(0, set, 0);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            int afterActivation = calls.ArgumentTableWrites;

            for (int draw = 0; draw < 100; draw++)
            {
                records.Record(0, set, 0);
                records.Flush(ref sink, Encoder, Epoch, segment: 0);
            }

            Assert.Equal(afterActivation, calls.ArgumentTableWrites);
        }

        /// <summary>
        /// THE SHADOW PASS'S SHAPE, FROZEN AT ONE CALL PER VISIBLE STAGE (M-R7). A thousand offsets-only rebinds
        /// of one set is a thousand times that and nothing else: no argument-table write, no re-derivation of an
        /// index, and no growth in the record.
        /// </summary>
        [Fact]
        public void AThousandOffsetsOnlyRebindsAreTwoCallsEach()
        {
            using var harness = new MetalRingHarness();
            var records = MetalBindRecords.ForGraphics();
            MetalBoundSet set = MetalBindProgram.Set(harness, frameBytes: 4);
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            records.SetIndexTable(MetalBindProgram.Table());
            records.Record(0, set, 0);
            records.Flush(ref sink, Encoder, Epoch, segment: 0);

            int afterActivation = calls.ArgumentTableWrites;

            // THE OFFSET ALTERNATES RATHER THAN CLIMBING, so every draw really does move it and none of the
            // thousand walks out of the 256-byte segment M-M4 bounds it by. A climbing offset would leave the
            // segment at draw 64 and this measurement would be about the refusal instead.
            for (uint draw = 1; draw <= 1000; draw++)
            {
                records.Record(0, set, 4 + (draw % 2 * 4));
                records.Flush(ref sink, Encoder, Epoch, segment: 0);
            }

            Assert.Equal(afterActivation + (1000 * 2), calls.ArgumentTableWrites);
            Assert.Equal(1000 * 2, calls.OffsetWrites.Count);

            // AND THE RECORD DID NOT GROW, which is rule 7's O(n) claim in one number: it follows the highest
            // SLOT and never the count of rebinds.
            Assert.Equal(1, records.RecordedSlotCount);
            Assert.Equal(4, records.SlotCapacity);
        }

        /// <summary>
        /// THE VERTEX-STREAM MARGINAL, WHICH IS THE ONE STATED AS A REGRESSION TARGET IN THE DESIGN'S OWN WORDS.
        /// The incumbent pays one <c>setVertexBuffer</c> per stream per draw unconditionally, because its cache
        /// is permanently cold. Here two streams cost ONE array call on the draw that binds them and ZERO on
        /// every draw after.
        /// </summary>
        [Fact]
        public void TwoVertexStreamsAreOneCallOnTheFirstDrawAndZeroAfterwards()
        {
            var streams = new MetalVertexStreamRecords();
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            streams.Record(0, new IntPtr(0xA), 0);
            streams.Record(1, new IntPtr(0xB), 0);
            streams.Flush(ref sink, Encoder, Epoch);

            Assert.Equal(1, calls.ArgumentTableWrites);

            for (int draw = 0; draw < 100; draw++)
            {
                streams.Record(0, new IntPtr(0xA), 0);
                streams.Record(1, new IntPtr(0xB), 0);
                streams.Flush(ref sink, Encoder, Epoch);
            }

            Assert.Equal(1, calls.ArgumentTableWrites);
        }

        /// <summary>
        /// EVERY SHIPPED GRAPHICS PROGRAM, ACTIVATED IN FULL, AND THE BOUND M-R6 PUTS ON IT. One array call per
        /// (space, stage) is at most SIX for a two-stage program, and any program needing more than that has an
        /// emission whose indices are not contiguous within a space, which the run cutter answers with an extra
        /// call rather than by padding a hole with nil.
        /// <para>
        /// THE HOLE COUNT IS THE MEASUREMENT WORTH HAVING HERE, and it is reported rather than asserted at a
        /// number: it is a property of what SPIRV-Cross emits for the shipped shaders, so it can move on a
        /// deliberate <c>Veldrid.SPIRV</c> bump, and the bound is what actually protects the budget.
        /// </para>
        /// </summary>
        [Fact]
        public void EveryShippedProgramActivatesWithinTheOneCallPerKindPerStageBound()
        {
            int programs = 0, worst = 0, withHoles = 0;
            string worstProgram = "";
            var report = new List<string>();

            foreach (ShippedGraphicsProgram program in D3D11ShaderProgramCatalog.GraphicsPrograms())
            {
                MetalShaderIndexTable table = MetalShaderBuild.Pair(
                    program.VertexGlsl, program.FragmentGlsl, program.Name).Table;

                var records = MetalBindRecords.ForGraphics();
                var calls = new FakeMetalEncoderCalls();
                var sink = new FakeMetalEncoderSink(calls);

                records.SetIndexTable(table);
                for (int slot = 0; slot < table.Layouts.Count; slot++)
                    records.Record((uint)slot, SetFor(table.Layouts[slot]), 0);

                records.Flush(ref sink, Encoder, Epoch, segment: 0);

                int distinctPairs = calls.ArrayWrites
                    .Select(w => (w.Stage, w.Space)).Distinct().Count();
                int holes = calls.ArrayWrites.Count - distinctPairs;

                if (holes > 0) withHoles++;
                if (calls.ArrayWrites.Count > worst)
                {
                    worst = calls.ArrayWrites.Count;
                    worstProgram = program.Name;
                }

                report.Add($"{program.Name}: {calls.ArrayWrites.Count} array calls over {distinctPairs} "
                    + $"(stage, space) pairs, {holes} extra from non-contiguous runs");

                // THE BOUND: at most one call per (space, stage) unless the emission left a hole in a run.
                Assert.True(calls.ArrayWrites.Count <= 6 + holes,
                    $"{program.Name} emitted {calls.ArrayWrites.Count} array calls");

                programs++;
            }

            foreach (string line in report) _output.WriteLine(line);
            _output.WriteLine($"programs: {programs.ToString(CultureInfo.InvariantCulture)}, worst full "
                + $"activation: {worst.ToString(CultureInfo.InvariantCulture)} calls ({worstProgram}), programs "
                + $"with a non-contiguous run: {withHoles.ToString(CultureInfo.InvariantCulture)}");

            Assert.True(programs >= 30, "the shipped catalog emptied out from under this measurement");
        }

        // A SET MATCHING A REFLECTED LAYOUT, with the FIRST buffer-space element declared dynamic, which is the
        // engine's own shape: one per-draw uniform window per set and everything else fixed. The resources are
        // fakes because a budget counts CALLS, and the handles a call carried are MetalBindFlushTests' subject.
        static MetalBoundSet SetFor(GpuResourceLayoutDescription layout)
        {
            var bindings = new MetalBoundResource[layout.Elements.Length];
            bool dynamicTaken = false;

            for (int i = 0; i < bindings.Length; i++)
            {
                MetalIndexSpace space = MetalIndexSpaces.For(layout.Elements[i].Kind);
                bool dynamic = space == MetalIndexSpace.Buffer && !dynamicTaken;
                dynamicTaken |= dynamic;

                bindings[i] = new MetalBoundResource(
                    space, new FakeMetalBindable(0x1000 + i), 0, 64, dynamic);
            }

            return new MetalBoundSet(bindings, dynamicTaken);
        }

        static readonly IntPtr Encoder = new(0x4D544C45);

        const ulong Epoch = 7;
    }
}
