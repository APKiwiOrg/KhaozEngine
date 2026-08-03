using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE SCHEDULE OF DECISION R5, RULE BY RULE, device-free. This is the shape that produced the 40x
    /// shadow-encode collapse, ported intact, and the spec calls it not negotiable, so each of its eight clauses
    /// has a test that fails when that clause alone is broken.
    /// <para>
    /// Everything here runs the SHIPPED schedule: <see cref="D3D11BindFlush"/> is what the real emitter uses, and
    /// <see cref="D3D11NativeTraceEmitter"/> supplies only the sink that turns a decided call into a name. So a
    /// failure here is a failure of the thing that ships, not of a harness that models it.
    /// </para>
    /// </summary>
    public sealed class D3D11BindFlushTests
    {
        // ---- Rule 1: a bind RECORDS ONLY ------------------------------------------------------------------

        /// <summary>
        /// A RESOURCE-SET BIND ISSUES NOTHING AT ALL. That is the whole premise: the incumbent activated a set at
        /// the bind, so the shadow pass paid a full activation per rebind, and the fix is that a bind is a compare
        /// and a store. The trace still shows WHERE the bind happened, and that marker is not a native call.
        /// </summary>
        [Fact]
        public void ABind_RecordsAndIssuesNothing()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.ModelLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ModelSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            harness.Log.Reset();

            emitter.SetGraphicsResourceSet(0, set);

            Assert.Equal(0, harness.Log.TotalCalls);
            Assert.Equal(1, harness.Log.Count(D3D11NativeCall.ResourceSetPending));
            Assert.Equal(D3D11SlotDirty.Full, harness.Binds.GraphicsDirty(0));
            Assert.Same(set, harness.Binds.RecordedGraphicsSet(0));
        }

        /// <summary>The three states are told apart by the comparison alone: a different set is a full
        /// activation, the same set at a different offset is offsets-only, and the same set at the same offset is
        /// nothing.</summary>
        [Fact]
        public void TheComparison_ProducesEachOfTheThreeStates()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet first = D3D11BindFixtures.ShadowSet(layout);
            using D3D11ResourceSet second = D3D11BindFixtures.ShadowSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;
            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));

            emitter.SetGraphicsResourceSet(0, first, 0);
            Assert.Equal(D3D11SlotDirty.Full, harness.Binds.GraphicsDirty(0));

            emitter.Draw(3, 1, 0, 0);
            emitter.SetGraphicsResourceSet(0, first, 256);
            Assert.Equal(D3D11SlotDirty.DynamicOffsetsOnly, harness.Binds.GraphicsDirty(0));

            emitter.Draw(3, 1, 0, 0);
            emitter.SetGraphicsResourceSet(0, first, 256);
            Assert.Equal(D3D11SlotDirty.Clean, harness.Binds.GraphicsDirty(0));

            emitter.SetGraphicsResourceSet(0, second, 256);
            Assert.Equal(D3D11SlotDirty.Full, harness.Binds.GraphicsDirty(0));
        }

        /// <summary>
        /// A SET BOUND WITH A DYNAMIC OFFSET OF ZERO AND ONE BOUND WITHOUT AN OFFSET ARE DIFFERENT BINDS, so the
        /// switch between the two forms is FULL rather than offsets-only. The seam carries them as two overloads
        /// and the op stream as two opcodes for that reason, and treating them as equal would take the
        /// offsets-only path, which skips textures and samplers entirely.
        /// </summary>
        [Fact]
        public void SwitchingBetweenTheOffsetAndNoOffsetForms_IsAFullActivation()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.ModelLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ModelSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;
            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));

            emitter.SetGraphicsResourceSet(0, set);
            emitter.Draw(3, 1, 0, 0);

            emitter.SetGraphicsResourceSet(0, set, 0);

            Assert.Equal(D3D11SlotDirty.Full, harness.Binds.GraphicsDirty(0));
        }

        // ---- Rule 2: the draw and the dispatch flush, through the pre-command hook --------------------------

        /// <summary>The binds land BEFORE the draw and the slot is clean afterwards, which is what "pre-command
        /// hook" means and what work-breakdown row 10 hangs the rest of its draw path on.</summary>
        [Fact]
        public void TheDraw_FlushesFirstAndThenIssues()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ShadowSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set, 0);
            harness.Log.Reset();

            emitter.DrawIndexed(6, 1, 0, 0, 0);

            Assert.Equal(
                new[] { $"VSSetConstantBuffers1(0,1,{harness.Log.Id(Ubo(set))}@0+16)", "DrawIndexedInstanced(6,1,0,0,0)" },
                harness.Log.Trace);
            Assert.Equal(D3D11SlotDirty.Clean, harness.Binds.GraphicsDirty(0));
        }

        /// <summary>Compute has its own dirty array (decision C1), so a compute set waits for a DISPATCH and a
        /// draw in between flushes nothing of it. Sharing one array would make every draw push the compute sets
        /// and every dispatch push the graphics ones.</summary>
        [Fact]
        public void AComputeSet_FlushesAtTheDispatchAndNotAtTheDraw()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout graphics = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceLayout compute = D3D11BindFixtures.Layout(
                D3D11BindFixtures.U("Params", GpuShaderStages.Compute),
                D3D11BindFixtures.StructRW("WorkBuf"));
            using D3D11ResourceSet computeSet = D3D11BindFixtures.Set(compute, new FakeBuffer(256), new FakeBuffer(64));
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(graphics));
            emitter.SetComputePipeline(new FakeComputePipeline(compute));
            emitter.SetComputeResourceSet(0, computeSet);
            harness.Log.Reset();

            emitter.Draw(3, 1, 0, 0);
            Assert.Equal(new[] { "DrawInstanced(3,1,0,0)" }, harness.Log.Trace);

            harness.Log.Reset();
            emitter.Dispatch(1, 1, 1);

            Assert.Equal(2, harness.Log.Count(D3D11NativeCall.CSSetConstantBuffers1)
                + harness.Log.Count(D3D11NativeCall.CSSetUnorderedAccessViews));
            Assert.Equal(1, harness.Log.Count(D3D11NativeCall.Dispatch));
        }

        // ---- Rule 4: slot order ----------------------------------------------------------------------------

        /// <summary>
        /// THE FLUSH WALKS SLOTS IN SLOT ORDER, not bind order, which is deliberate and observable. The one case
        /// where the two differ is a resource bound two incompatible ways at once, which Direct3D 11 cannot honour
        /// either way, so slot order is chosen for being deterministic.
        /// </summary>
        [Fact]
        public void TheFlush_WalksSlotsInSlotOrderRatherThanBindOrder()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout first = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceLayout second = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet zero = D3D11BindFixtures.ShadowSet(first);
            using D3D11ResourceSet one = D3D11BindFixtures.ShadowSet(second);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(first, second));
            emitter.SetGraphicsResourceSet(1, one, 0);
            emitter.SetGraphicsResourceSet(0, zero, 0);
            harness.Log.Reset();

            emitter.Draw(3, 1, 0, 0);

            // Slot zero numbers at b0 and slot one past it at b1, and the b0 line comes first because the walk is
            // by slot. Bind order put slot one first.
            Assert.Equal(
                new[]
                {
                    $"VSSetConstantBuffers1(0,1,{harness.Log.Id(Ubo(zero))}@0+16)",
                    $"VSSetConstantBuffers1(1,1,{harness.Log.Id(Ubo(one))}@0+16)",
                    "DrawInstanced(3,1,0,0)",
                },
                harness.Log.Trace);
        }

        // ---- Rule 5: the pipeline-switch drain, under the OUTGOING layouts ---------------------------------

        /// <summary>
        /// THE DRAIN HAPPENS UNDER THE PIPELINE BEING LEFT, and this test is built so the two answers differ. The
        /// outgoing pipeline puts two constant buffers before slot one, so the pending set numbers at <c>b2</c>.
        /// The incoming one puts a single constant buffer there, so the same set under the same slot would number
        /// at <c>b1</c>. Draining after the switch would therefore bind the shadow UBO at the wrong register,
        /// which compiles, draws and renders the wrong constants.
        /// </summary>
        [Fact]
        public void APipelineSwitch_DrainsPendingSetsUnderTheOutgoingLayouts()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout twoUbos = D3D11BindFixtures.Layout(
                D3D11BindFixtures.U("A", GpuShaderStages.Vertex),
                D3D11BindFixtures.U("B", GpuShaderStages.Vertex));
            using D3D11ResourceLayout oneUbo = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceLayout shadow = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet pending = D3D11BindFixtures.ShadowSet(shadow);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(twoUbos, shadow));
            emitter.SetGraphicsResourceSet(1, pending, 0);
            harness.Log.Reset();

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(oneUbo, shadow));

            Assert.Equal($"VSSetConstantBuffers1(2,1,{harness.Log.Id(Ubo(pending))}@0+16)", harness.Log.Trace[0]);
            Assert.Equal(D3D11SlotDirty.Clean, harness.Binds.GraphicsDirty(1));
        }

        /// <summary>With NO pipeline bound there is nothing to number against, so the marks stay pending and the
        /// first draw under the incoming pipeline pays them. Inventing a numbering would be worse than deferring,
        /// and dropping the mark would lose the bind entirely.</summary>
        [Fact]
        public void ASetRecordedBeforeAnyPipeline_StaysPendingUntilTheFirstDraw()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ShadowSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetGraphicsResourceSet(0, set, 0);
            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));

            Assert.Equal(D3D11SlotDirty.Full, harness.Binds.GraphicsDirty(0));
            Assert.Equal(0, harness.Log.Count(D3D11NativeCall.VSSetConstantBuffers1));

            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(1, harness.Log.Count(D3D11NativeCall.VSSetConstantBuffers1));
        }

        /// <summary>
        /// AND THE SWITCH FORGETS THE RECORDS, which is the half a drain alone does not give you and the half
        /// that is silent when it is missing. The same set is rebound at the same slot after the switch, and the
        /// incoming pipeline's preceding layout renumbers that slot, so a record that survived would compare
        /// equal, mark the slot clean and issue NOTHING: the set would stay physically at the outgoing
        /// pipeline's registers while the incoming pipeline read the new ones. Wrong constants, no throw, no log.
        /// </summary>
        [Fact]
        public void APipelineSwitchThatRenumbersASlot_ReactivatesTheSameSetAtTheNewBase()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout oneUbo = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceLayout twoUbos = D3D11BindFixtures.Layout(
                D3D11BindFixtures.U("A", GpuShaderStages.Vertex),
                D3D11BindFixtures.U("B", GpuShaderStages.Vertex));
            using D3D11ResourceLayout shadow = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ShadowSet(shadow);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            // Under the first pipeline slot one numbers at b1, because one constant buffer precedes it.
            emitter.SetPipeline(D3D11BindFixtures.Pipeline(oneUbo, shadow));
            emitter.SetGraphicsResourceSet(1, set, 0);
            harness.Log.Reset();
            emitter.Draw(3, 1, 0, 0);
            Assert.Equal(
                new[] { $"VSSetConstantBuffers1(1,1,{harness.Log.Id(Ubo(set))}@0+16)" },
                harness.BindTrace());

            // Under the second it numbers at b2, and the SAME set at the SAME slot and offset is bound again.
            emitter.SetPipeline(D3D11BindFixtures.Pipeline(twoUbos, shadow));
            harness.Log.Reset();
            emitter.SetGraphicsResourceSet(1, set, 0);

            Assert.Equal(D3D11SlotDirty.Full, harness.Binds.GraphicsDirty(1));
            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(
                new[] { $"VSSetConstantBuffers1(2,1,{harness.Log.Id(Ubo(set))}@0+16)" },
                harness.BindTrace());
        }

        /// <inheritdoc cref="APipelineSwitchThatRenumbersASlot_ReactivatesTheSameSetAtTheNewBase"/>
        [Fact]
        public void AComputePipelineSwitchThatRenumbersASlot_ReactivatesTheSameSetAtTheNewBase()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout oneUbo = D3D11BindFixtures.Layout(
                D3D11BindFixtures.U("A", GpuShaderStages.Compute));
            using D3D11ResourceLayout twoUbos = D3D11BindFixtures.Layout(
                D3D11BindFixtures.U("A", GpuShaderStages.Compute),
                D3D11BindFixtures.U("B", GpuShaderStages.Compute));
            using D3D11ResourceLayout work = D3D11BindFixtures.Layout(
                D3D11BindFixtures.U("Params", GpuShaderStages.Compute, dynamic: true));
            using D3D11ResourceSet set = D3D11BindFixtures.Set(work, new GpuBufferRange(new FakeBuffer(4096), 0, 64));
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetComputePipeline(new FakeComputePipeline(oneUbo, work));
            emitter.SetComputeResourceSet(1, set, 0);
            harness.Log.Reset();
            emitter.Dispatch(1, 1, 1);
            Assert.Equal(
                new[] { $"CSSetConstantBuffers1(1,1,{harness.Log.Id(Ubo(set))}@0+16)" },
                harness.BindTrace());

            emitter.SetComputePipeline(new FakeComputePipeline(twoUbos, work));
            harness.Log.Reset();
            emitter.SetComputeResourceSet(1, set, 0);

            Assert.Equal(D3D11SlotDirty.Full, harness.Binds.ComputeDirty(1));
            emitter.Dispatch(1, 1, 1);

            Assert.Equal(
                new[] { $"CSSetConstantBuffers1(2,1,{harness.Log.Id(Ubo(set))}@0+16)" },
                harness.BindTrace());
        }

        /// <summary>
        /// A RE-BIND OF THE PIPELINE ALREADY CURRENT DRAINS NOTHING AND FORGETS NOTHING. The numbering has not
        /// moved, so there is nothing for the drain to be wrong about and nothing for the wipe to protect, and a
        /// renderer that rebinds its pipeline defensively between two draws would otherwise wipe the records it
        /// just made and pay a full activation per bind, which is the #418 cost by another door.
        /// </summary>
        [Fact]
        public void ARedundantPipelineRebind_DrainsNothingAndForgetsNothing()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ShadowSet(layout);
            D3D11StateCacheTests.FakeD3D11Pipeline pipeline = D3D11BindFixtures.Pipeline(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(pipeline);
            emitter.SetGraphicsResourceSet(0, set, 0);
            harness.Log.Reset();

            emitter.SetPipeline(pipeline);

            // Nothing at all: the seven state objects are already bound and the pending mark is still pending.
            Assert.Empty(harness.Log.Trace);
            Assert.Equal(D3D11SlotDirty.Full, harness.Binds.GraphicsDirty(0));
            Assert.Same(set, harness.Binds.RecordedGraphicsSet(0));

            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(
                new[] { $"VSSetConstantBuffers1(0,1,{harness.Log.Id(Ubo(set))}@0+16)" },
                harness.BindTrace());
        }

        /// <inheritdoc cref="ARedundantPipelineRebind_DrainsNothingAndForgetsNothing"/>
        [Fact]
        public void ARedundantComputePipelineRebind_DrainsNothingAndForgetsNothing()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout work = D3D11BindFixtures.Layout(
                D3D11BindFixtures.U("Params", GpuShaderStages.Compute, dynamic: true));
            using D3D11ResourceSet set = D3D11BindFixtures.Set(work, new GpuBufferRange(new FakeBuffer(4096), 0, 64));
            var pipeline = new FakeComputePipeline(work);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetComputePipeline(pipeline);
            emitter.SetComputeResourceSet(0, set, 0);
            harness.Log.Reset();

            emitter.SetComputePipeline(pipeline);

            // The compute shader bind itself is deliberately unguarded (its redundancy cache belongs with the
            // rest of the compute schedule), so the shader call is expected and the DRAIN is what must be absent.
            Assert.Equal(new[] { $"CSSetShader({harness.Log.Id(pipeline)})" }, harness.Log.Trace);
            Assert.Equal(D3D11SlotDirty.Full, harness.Binds.ComputeDirty(0));
        }

        /// <summary>A set at a slot the current pipeline does not declare is a pipeline-and-set mismatch, and it
        /// is refused with the register scheme's own message rather than binding at a base summed over every
        /// layout, which is the one wrong answer here that renders instead of throwing.</summary>
        [Fact]
        public void ASetAtASlotThePipelineDoesNotDeclare_IsRefusedAtTheFlush()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ShadowSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(1, set, 0);

            Assert.Throws<ArgumentOutOfRangeException>(() => emitter.Draw(3, 1, 0, 0));
        }

        // ---- Rule 6: a slot whose recorded set has gone null ------------------------------------------------

        /// <summary>
        /// A NULL RECORD IS SKIPPED, not unbound. The registers the slot used belong to that slot alone, so
        /// leaving them is invisible to every shader the next draw runs, and unbinding them would spend native
        /// calls on a slot nobody reads. The slot still goes clean, so the skip happens once.
        /// </summary>
        [Fact]
        public void ASlotWhoseRecordedSetWentNull_IsSkipped()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.ModelLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ModelSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set);
            emitter.Draw(3, 1, 0, 0);

            harness.Binds.RecordGraphics(0, null, 0, hasDynamicOffset: false);
            Assert.Equal(D3D11SlotDirty.Full, harness.Binds.GraphicsDirty(0));
            harness.Log.Reset();

            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(new[] { "DrawInstanced(3,1,0,0)" }, harness.Log.Trace);
            Assert.Equal(D3D11SlotDirty.Clean, harness.Binds.GraphicsDirty(0));
        }

        // ---- Rule 7: repeated marks collapse to one flush ---------------------------------------------------

        /// <summary>Five binds between two draws are ONE activation. The collapse is what makes a renderer free to
        /// bind defensively, which several of them do.</summary>
        [Fact]
        public void RepeatedMarksBetweenTwoDraws_CollapseToOneFlush()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.ModelLayout();
            using D3D11ResourceSet first = D3D11BindFixtures.ModelSet(layout);
            using D3D11ResourceSet second = D3D11BindFixtures.ModelSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, first);
            emitter.SetGraphicsResourceSet(0, second);
            emitter.SetGraphicsResourceSet(0, first);
            emitter.SetGraphicsResourceSet(0, second);
            harness.Log.Reset();

            emitter.Draw(3, 1, 0, 0);

            // Four binds, one activation of the LAST of them: the model set's four calls plus the draw.
            Assert.Equal(5, harness.Log.TotalCalls);
            Assert.Equal(1, harness.Log.Count(D3D11NativeCall.PSSetShaderResources));
        }

        /// <summary>
        /// AND THE COLLAPSE TAKES THE GREATER OF THE MARKS. An offsets-only rebind arriving after a full one that
        /// has not been flushed yet still owes a full activation: the textures were never pushed. Lowering the
        /// state to offsets-only there would leave the pixel stage sampling whatever the last set left bound.
        /// </summary>
        [Fact]
        public void AnOffsetsOnlyMarkOverAPendingFullOne_StaysFull()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.WaterLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.WaterSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set, 0);
            emitter.SetGraphicsResourceSet(0, set, 512);

            Assert.Equal(D3D11SlotDirty.Full, harness.Binds.GraphicsDirty(0));
            harness.Log.Reset();

            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(2, harness.Log.Count(D3D11NativeCall.VSSetShaderResources)
                + harness.Log.Count(D3D11NativeCall.PSSetShaderResources));
        }

        /// <summary>The dedup half of the same rule: rebinding what the slot already holds, at the same offset,
        /// costs nothing at the next draw. The shadow pass leans on this every time a mesh reuses the previous
        /// mesh's set.</summary>
        [Fact]
        public void RebindingTheSameSetAtTheSameOffset_CostsNothing()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.ModelLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ModelSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set);
            emitter.Draw(3, 1, 0, 0);
            harness.Log.Reset();

            emitter.SetGraphicsResourceSet(0, set);
            emitter.Draw(3, 1, 0, 0);

            // The bind is in the trace and is not a call: the marker holds its place and the draw is the only
            // thing the device sees.
            Assert.Equal(
                new[] { $"ResourceSetPending(gfx,0,{harness.Log.Id(set)})", "DrawInstanced(3,1,0,0)" },
                harness.Log.Trace);
            Assert.Equal(1, harness.Log.TotalCalls);
        }

        // ---- Rule 8: the bound record is KEYED and does not grow per rebind ---------------------------------

        /// <summary>
        /// THE RECORD IS KEYED BY SLOT AND CONSTANT IN THE NUMBER OF REBINDS. The hot path is thousands of
        /// offsets-only rebinds of ONE set per frame, so a record that appended per rebind, or that searched a
        /// growing list to compare, would make the frame O(n squared) in the rebind count. Asserted as a size that
        /// does not move across four thousand rebinds, which is the property rather than a timing.
        /// </summary>
        [Fact]
        public void ThousandsOfRebindsOfOneSet_DoNotGrowTheRecord()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ShadowSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set, 0);
            emitter.Draw(3, 1, 0, 0);
            int afterOne = harness.Binds.RecordedSlotCapacity;

            for (int i = 1; i <= 4000; i++)
            {
                emitter.SetGraphicsResourceSet(0, set, (uint)(i * 256));
                emitter.Draw(3, 1, 0, 0);
            }

            Assert.Equal(afterOne, harness.Binds.RecordedSlotCapacity);
            Assert.Same(set, harness.Binds.RecordedGraphicsSet(0));
        }

        // ---- The ClearState boundary -----------------------------------------------------------------------

        /// <summary>
        /// THE ONE <c>ClearState</c> PER REPLAY FORGETS THE SETS TOO. After it the context holds nothing, so a
        /// record that survived would let a rebind of the same set at the same offset be marked clean and the draw
        /// would run against registers holding nothing. Silently: no throw, no log, a black or garbage frame.
        /// </summary>
        [Fact]
        public void TheClearStateOpeningAReplay_ForgetsTheBoundSets()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.ModelLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ModelSet(layout);
            D3D11StateCacheTests.FakeD3D11Pipeline pipeline = D3D11BindFixtures.Pipeline(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(pipeline);
            emitter.SetGraphicsResourceSet(0, set);
            emitter.Draw(3, 1, 0, 0);
            emitter.End();

            emitter.Begin();
            harness.Log.Reset();
            emitter.SetPipeline(pipeline);
            emitter.SetGraphicsResourceSet(0, set);
            emitter.Draw(3, 1, 0, 0);

            // The whole set again, because the context genuinely holds nothing after a ClearState.
            Assert.Equal(1, harness.Log.Count(D3D11NativeCall.PSSetShaderResources));
            Assert.Equal(1, harness.Log.Count(D3D11NativeCall.PSSetSamplers));
            Assert.Equal(2, harness.Log.Count(D3D11NativeCall.VSSetConstantBuffers1)
                + harness.Log.Count(D3D11NativeCall.PSSetConstantBuffers1));
        }

        // ---- The ring bracket at the flush point (decisions U2 and R2) --------------------------------------

        /// <summary>
        /// ON THE IMMEDIATE DRIVER THE FLUSH UNMAPS THE RINGS FIRST. That driver issues draws as the seam is
        /// called, so a ring mapped by a record-time uniform write is still mapped when the draw binds it, and
        /// Direct3D 11 does not permit a draw against a mapped resource. Asserted as trace POSITION rather than as
        /// a count, because an unmap after the bind is exactly as wrong as no unmap at all.
        /// </summary>
        [Fact]
        public void OnTheImmediateDriver_TheFlushUnmapsTheRingsBeforeIssuingAnyBind()
        {
            var log = new D3D11NativeCallLog();
            var submitLock = new object();
            var completion = new FakeD3D11Completion();
            var allocator = new D3D11RingAllocator(3, completion, submitLock);
            using var memory = new D3D11BindFixtures.TracedRingMemory(
                D3D11UniformRing.TotalBytesFor(256, 3), log);
            var ring = new D3D11UniformRing(allocator, memory, 256);
            var buffer = new FakeRingBackedBuffer(ring);

            var state = new D3D11DeviceState(new D3D11BindFlush(
                ringsUnmappedBeforeCommands: D3D11BindFlush.RingsFor(D3D11RecordMode.Immediate, allocator)));
            var emitter = new D3D11NativeTraceEmitter(state, log);
            using D3D11ResourceLayout layout = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ShadowSet(layout, buffer);

            emitter.Begin();
            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set, 0);

            // The record-time uniform write, written straight at the ring: routing an UpdateBuffer to it is
            // decision U4 and belongs to the real emitter's write path, and what this test is about is what the
            // MAPPING does to the draw that follows.
            ring.Write(0, new byte[] { 1, 2, 3, 4 });
            Assert.True(ring.IsMapped);
            log.Reset();

            emitter.Draw(3, 1, 0, 0);

            Assert.False(ring.IsMapped);
            Assert.Equal(
                new[] { "Unmap()", $"VSSetConstantBuffers1(0,1,{log.Id(buffer)}@0+16)", "DrawInstanced(3,1,0,0)" },
                log.Trace);
        }

        /// <summary>
        /// AND IT UNMAPS EVEN WHEN NOTHING IS DIRTY, which is the half that is easy to get wrong by putting the
        /// unmap inside the dirty check. A draw with a clean slot still draws against the constant buffers an
        /// earlier flush bound, and a uniform write since then has re-mapped the ring underneath them.
        /// </summary>
        [Fact]
        public void ADrawWithNothingDirty_StillUnmapsTheRings()
        {
            var log = new D3D11NativeCallLog();
            var submitLock = new object();
            var allocator = new D3D11RingAllocator(3, new FakeD3D11Completion(), submitLock);
            using var memory = new D3D11BindFixtures.TracedRingMemory(
                D3D11UniformRing.TotalBytesFor(256, 3), log);
            var ring = new D3D11UniformRing(allocator, memory, 256);
            var buffer = new FakeRingBackedBuffer(ring);

            var state = new D3D11DeviceState(new D3D11BindFlush(
                ringsUnmappedBeforeCommands: D3D11BindFlush.RingsFor(D3D11RecordMode.Immediate, allocator)));
            var emitter = new D3D11NativeTraceEmitter(state, log);
            using D3D11ResourceLayout layout = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ShadowSet(layout, buffer);

            emitter.Begin();
            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set, 0);
            emitter.Draw(3, 1, 0, 0);

            ring.Write(0, new byte[] { 1, 2, 3, 4 });
            Assert.True(ring.IsMapped);
            log.Reset();

            emitter.Draw(3, 1, 0, 0);

            Assert.False(ring.IsMapped);
            Assert.Equal(new[] { "Unmap()", "DrawInstanced(3,1,0,0)" }, log.Trace);
        }

        /// <summary>The deferred driver wires no allocator into the flush, so a draw there touches the ring not at
        /// all. Its <c>Submit</c> already unmapped inside the lock it replays under, and an unmap per draw would
        /// be an uncontended lock a thousand times a frame for a call that can never do anything.</summary>
        [Fact]
        public void OnTheDeferredDriver_TheFlushDoesNotTouchTheRings()
        {
            var harness = new D3D11BindFixtures.Harness();

            Assert.False(harness.Binds.UnmapsRingsBeforeCommands);
        }

        // ---- Fixtures ---------------------------------------------------------------------------------------

        static IGpuBuffer? Ubo(D3D11ResourceSet set)
        {
            foreach (D3D11BoundResource binding in set.Bindings)
                if (binding.Kind == GpuResourceKind.UniformBuffer) return binding.Buffer;

            return null;
        }

        /// <summary>A compute pipeline that answers its resource layouts, which is all the bind flush asks of one.
        /// The rest of the compute path is work-breakdown row 12.</summary>
        internal sealed class FakeComputePipeline : IGpuComputePipeline, ID3D11PipelineLayouts
        {
            internal FakeComputePipeline(params D3D11ResourceLayout[] layouts) => ResourceLayouts = layouts;

            public D3D11ResourceLayout[] ResourceLayouts { get; }

            public void Dispose()
            {
            }
        }
    }
}
