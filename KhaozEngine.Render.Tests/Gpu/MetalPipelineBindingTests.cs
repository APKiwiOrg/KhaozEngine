using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION M-R8, THE GUARD THE INCUMBENT LACKS, driven through the real command list. Section 6.3 clause 5,
    /// work-breakdown row 11 (https://github.com/APKiwiOrg/KhaozEngine/issues/577).
    ///
    /// <para><b>WHAT THIS EXISTS TO CATCH IS A COST RATHER THAN A CORRUPTION, and that is why it needs a test at
    /// all.</b> <c>MTLCommandList.SetPipelineCore</c> stores the pipeline, clears the whole active-set array and
    /// sets its changed flag on EVERY call, so a redundant bind costs a five-call state re-emit plus a full
    /// re-activation of every resource set, and nothing about that is visible in a rendered frame. A guard that
    /// silently stopped working would be invisible in exactly the same way, so the assertion is behavioural: bind
    /// the same pipeline twice and the second one changes nothing.</para>
    ///
    /// <para><b>THE TWO INVALIDATION RULES ARE THE OTHER HALF, and they are what a bool would collapse.</b> WHICH
    /// pipeline is bound survives an encoder boundary. WHETHER its state block has reached the current encoder
    /// does not, because Metal's bound pipeline state is a property of the encoder (M-R4). The rows below drive
    /// both, through the same <c>MetalEncoderScope</c> a real recording uses, because a record that survived the
    /// wrong one of the two is either a redundant re-emit every draw or a draw with no pipeline state at
    /// all.</para>
    ///
    /// <para><b>THE PIPELINES HERE HOLD NIL HANDLES, which is what makes this device-free.</b> A
    /// <c>MetalGraphicsPipeline</c> is a liveness token, a resolved plan and two Objective-C handles, and only an
    /// emission or a disposal would ever touch the handles. <c>SetPipeline</c> reaches neither.</para>
    /// </summary>
    public sealed class MetalPipelineBindingTests : IDisposable
    {
        /// <inheritdoc/>
        public void Dispose() => _harness.Dispose();

        readonly MetalRingHarness _harness = new();

        /// <summary>THE ONE THAT MATTERS. The second bind of the pipeline already bound changes nothing at
        /// all.</summary>
        [Fact]
        public void ARedundantPipelineBindChangesNothing()
        {
            MetalPipelineBinding binding = new();
            MetalGraphicsPipeline first = Pipeline();

            Assert.True(binding.BindGraphics(first));

            // The pre-draw flush emits the state block and stamps it against the encoder it went into.
            binding.MarkGraphicsStateBlockEmitted(7);
            Assert.False(binding.NeedsGraphicsStateBlock(7));

            // M-R8: the redundant bind reports no change, so the caller does none of the work a switch owes, and
            // the stamp survives, so the next draw re-emits nothing.
            Assert.False(binding.BindGraphics(first));
            Assert.False(binding.NeedsGraphicsStateBlock(7));
            Assert.Same(first, binding.Graphics);
        }

        /// <summary>The other half, without which the row above is satisfied by a guard that never lets anything
        /// through: a DIFFERENT pipeline changes the binding and puts the state block back.</summary>
        [Fact]
        public void ADifferentPipelineChangesTheBindingAndOwesItsStateBlock()
        {
            MetalPipelineBinding binding = new();
            MetalGraphicsPipeline first = Pipeline();
            MetalGraphicsPipeline second = Pipeline();

            binding.BindGraphics(first);
            binding.MarkGraphicsStateBlockEmitted(7);

            Assert.True(binding.BindGraphics(second));
            Assert.Same(second, binding.Graphics);
            Assert.True(binding.NeedsGraphicsStateBlock(7));
        }

        /// <summary>
        /// M-R4 ON THE PIPELINE RECORD: an encoder boundary invalidates the state BLOCK and not the binding. The
        /// incumbent has to remember to re-set its flag by hand inside <c>EndCurrentRenderPass</c>, and this
        /// falls out of the epoch instead.
        /// </summary>
        [Fact]
        public void AnEncoderBoundaryInvalidatesTheStateBlockAndNotTheBinding()
        {
            MetalEncoderScope scope = new(new FakeMetalEncoderSink(new FakeMetalEncoderCalls()));
            scope.BeginRecording(new IntPtr(0x100));

            MetalPipelineBinding binding = new();
            MetalGraphicsPipeline pipeline = Pipeline();

            scope.EnsureRenderEncoder(new IntPtr(0xD5));
            binding.BindGraphics(pipeline);
            binding.MarkGraphicsStateBlockEmitted(scope.Epoch);
            Assert.False(binding.NeedsGraphicsStateBlock(scope.Epoch));

            // The record-time upload that ends the render encoder, which is 2.1's whole subject.
            scope.EnsureBlitEncoder();
            scope.EnsureRenderEncoder(new IntPtr(0xD5));

            // The pipeline is still the one the recorder intends, and its state has to be written into the new
            // encoder before the next draw.
            Assert.Same(pipeline, binding.Graphics);
            Assert.True(binding.NeedsGraphicsStateBlock(scope.Epoch));
        }

        /// <summary>A record that was never marked reads as owing its block, which is what a
        /// default-constructed stamp has to answer so the FIRST draw of a recording emits.</summary>
        [Fact]
        public void AFreshBindingOwesBothBlocks()
        {
            MetalPipelineBinding binding = new();

            Assert.True(binding.NeedsGraphicsStateBlock(1));
            Assert.True(binding.NeedsComputeStateBlock(1));
            Assert.Null(binding.Graphics);
            Assert.Null(binding.Compute);
        }

        /// <summary>The compute sibling carries the same guard, because a redundant compute bind costs the same
        /// re-activation on the incumbent.</summary>
        [Fact]
        public void ARedundantComputePipelineBindChangesNothing()
        {
            MetalPipelineBinding binding = new();
            MetalComputePipeline pipeline = ComputePipeline();

            Assert.True(binding.BindCompute(pipeline));
            binding.MarkComputeStateBlockEmitted(3);

            Assert.False(binding.BindCompute(pipeline));
            Assert.False(binding.NeedsComputeStateBlock(3));

            Assert.True(binding.BindCompute(ComputePipeline()));
            Assert.True(binding.NeedsComputeStateBlock(3));
        }

        /// <summary>
        /// SetPipeline records through the list, and a redundant bind is redundant there too. Driven through the
        /// real list because that is the member the seam calls and the guard is only worth anything where a
        /// consumer can reach it.
        /// </summary>
        [Fact]
        public void TheListRecordsTheBoundPipelineAndGuardsARedundantBind()
        {
            MetalCommandList list = NewList();
            MetalGraphicsPipeline pipeline = Pipeline();

            list.Begin();
            list.SetPipeline(pipeline);

            Assert.Same(pipeline, list.Pipelines.Graphics);
            list.Pipelines.MarkGraphicsStateBlockEmitted(list.Encoders.Epoch);

            list.SetPipeline(pipeline);
            Assert.False(list.Pipelines.NeedsGraphicsStateBlock(list.Encoders.Epoch));

            list.SetComputePipeline(ComputePipeline());
            Assert.NotNull(list.Pipelines.Compute);

            list.End();
        }

        /// <summary>
        /// A NEW RECORDING FORGETS BOTH PIPELINES, which is the one reset the binding needs and which lives in
        /// <c>Begin</c> with every other recorder reset. A pipeline carried over would let the first draw of the
        /// next recording skip an emission that never happened on its command buffer.
        /// </summary>
        [Fact]
        public void ANewRecordingForgetsBothPipelines()
        {
            MetalCommandList list = NewList();

            list.Begin();
            list.SetPipeline(Pipeline());
            list.SetComputePipeline(ComputePipeline());
            list.End();

            list.Begin();
            Assert.Null(list.Pipelines.Graphics);
            Assert.Null(list.Pipelines.Compute);
            Assert.True(list.Pipelines.NeedsGraphicsStateBlock(list.Encoders.Epoch));
            list.End();
        }

        /// <summary>Binding outside a recording is refused by name, because a bound pipeline is state of the
        /// recording rather than of the list.</summary>
        [Fact]
        public void BindingOutsideARecording_IsRefused()
        {
            MetalCommandList list = NewList();

            Assert.Contains("not recording",
                Assert.Throws<InvalidOperationException>(() => list.SetPipeline(Pipeline())).Message,
                StringComparison.Ordinal);
            Assert.Contains("not recording",
                Assert.Throws<InvalidOperationException>(
                    () => list.SetComputePipeline(ComputePipeline())).Message,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Another device's pipeline is refused, and so is null. The ownership check runs BEFORE the recording
        /// check, in the shape <c>UpdateBuffer</c> settled on: a caller passing the wrong device's pipeline has
        /// made the same mistake whether or not this list is recording.
        /// </summary>
        [Fact]
        public void AnotherDevicesPipelineOrNull_IsRefused()
        {
            MetalCommandList list = NewList();
            list.Begin();

            Assert.Throws<ArgumentNullException>(() => list.SetPipeline(null!));
            Assert.Contains("DIFFERENT native Metal device",
                Assert.Throws<ArgumentException>(
                    () => list.SetPipeline(Pipeline(new FakeMetalDeviceLiveness()))).Message,
                StringComparison.Ordinal);

            list.End();
        }

        /// <summary>
        /// A DISPOSED PIPELINE IS REFUSED AT THE BIND, BY NAME, and both kinds are.
        ///
        /// <para><b>THIS IS THE ONE REFUSAL ON THIS PATH THAT IS ABOUT MEMORY RATHER THAN ABOUT BELIEF.</b> A
        /// disposed resource LAYOUT or SET releases nothing at all on this backend, so refusing one is about the
        /// caller thinking the declaration is still theirs. A disposed PIPELINE has released its
        /// <c>MTLRenderPipelineState</c> and its <c>MTLDepthStencilState</c>, so accepting one records a dangling
        /// handle that the pre-draw flush later sets on a live encoder: a use-after-free inside the driver, at a
        /// call several steps removed from the mistake.</para>
        /// </summary>
        [Fact]
        public void ADisposedPipeline_IsRefused()
        {
            MetalCommandList list = NewList();
            list.Begin();

            MetalGraphicsPipeline graphics = Pipeline();
            graphics.Dispose();

            ObjectDisposedException refused = Assert.Throws<ObjectDisposedException>(
                () => list.SetPipeline(graphics));
            Assert.Contains("already disposed", refused.Message, StringComparison.Ordinal);
            Assert.Contains("MTLRenderPipelineState", refused.Message, StringComparison.Ordinal);

            MetalComputePipeline compute = ComputePipeline();
            compute.Dispose();

            ObjectDisposedException refusedCompute = Assert.Throws<ObjectDisposedException>(
                () => list.SetComputePipeline(compute));
            Assert.Contains("already disposed", refusedCompute.Message, StringComparison.Ordinal);
            Assert.Contains("MTLComputePipelineState", refusedCompute.Message, StringComparison.Ordinal);

            // NOTHING WAS RECORDED BY EITHER, which is the half that would still be wrong if the refusal landed
            // after the record instead of before it.
            Assert.Null(list.Pipelines.Graphics);
            Assert.Null(list.Pipelines.Compute);

            list.End();
        }

        /// <summary>
        /// AND THE STATE ACCESSORS REFUSE TOO, which is the second door into the same released object.
        /// <c>MetalShaderSet.FunctionFor</c> set this precedent for exactly this reason: a disposed set answers
        /// nothing rather than handing back a released handle. Everything on these types that is MANAGED data
        /// keeps answering, because none of it was released and a reader of it is asking a question disposal did
        /// not change.
        /// </summary>
        [Fact]
        public void ADisposedPipelinesReleasedHandles_AreRefusedAndItsManagedDataIsNot()
        {
            MetalGraphicsPipeline graphics = Pipeline();
            MetalShaderIndexTable table = graphics.Table;
            graphics.Dispose();

            Assert.Throws<ObjectDisposedException>(() => graphics.RenderState);
            Assert.Throws<ObjectDisposedException>(() => graphics.DepthStencilState);

            // The plan is managed data that disposal released nothing of, so it still answers.
            Assert.Same(table, graphics.Table);
            Assert.False(graphics.State.ScissorTestEnabled);

            MetalComputePipeline compute = ComputePipeline();
            compute.Dispose();

            Assert.Throws<ObjectDisposedException>(() => compute.State);
            Assert.NotNull(compute.Table);
        }

        /// <summary>
        /// M-R9 THROUGH THE LIST: a pipeline switch adopts the incoming program's index table, and invalidates
        /// the recorded binds only where the table actually differs.
        ///
        /// <para><b>THE SAME-TABLE ARM IS THE ONE WITH A COST BEHIND IT.</b> Row 10 content-deduplicates the
        /// tables per device, so two programs that map every element to the same indices SHARE one instance and
        /// this is a reference compare that answers "nothing to invalidate". Without the identity half, every
        /// pipeline switch in a frame would throw away every recorded bind and pay a full re-activation, which is
        /// the incumbent's behaviour and the thing M-R8 and M-R9 exist to stop.</para>
        /// </summary>
        [Fact]
        public void APipelineSwitchAdoptsTheIncomingTableAndInvalidatesOnlyOnARealChange()
        {
            MetalShaderIndexTable shared = EmptyTable();

            MetalCommandList list = NewList();
            list.Begin();

            Assert.Null(list.GraphicsBinds.IndexTable);

            list.SetPipeline(Pipeline(table: shared));
            Assert.Same(shared, list.GraphicsBinds.IndexTable);

            // A recorded slot, flushed clean, so the invalidation below is visible as a change rather than as the
            // state a fresh record is already in.
            list.GraphicsBinds.Record(0, MetalBindProgram.Set(_harness), 0);
            var sink = new FakeMetalEncoderSink(new FakeMetalEncoderCalls());
            list.GraphicsBinds.Flush(ref sink, new IntPtr(0xD5), list.Encoders.Epoch, segment: 0);
            Assert.False(list.GraphicsBinds.IsDirty(0));

            // A DIFFERENT pipeline carrying the SAME table instance: the switch is real (M-R8 lets it through)
            // and the binds survive it.
            list.SetPipeline(Pipeline(table: shared));
            Assert.Same(shared, list.GraphicsBinds.IndexTable);
            Assert.False(list.GraphicsBinds.IsDirty(0));

            // A pipeline whose table maps its elements somewhere else: every recorded slot owes a full rebind,
            // because the resources sitting in the argument table are at indices the incoming program does not
            // read.
            MetalShaderIndexTable other = EmptyTable();
            list.SetPipeline(Pipeline(table: other));
            Assert.Same(other, list.GraphicsBinds.IndexTable);
            Assert.True(list.GraphicsBinds.IsDirty(0));

            list.End();
        }

        /// <summary>
        /// AND THE COMPUTE SWITCH DOES IT TOO, which is the arm most easily lost: the graphics site carries a
        /// loud comment about M-R9 and this one carries none, and <c>BindCompute</c>'s change bool is the one the
        /// caller has no other reason to read. A compute path that skipped this would bind a dispatch's resources
        /// at the previous kernel's indices.
        /// </summary>
        [Fact]
        public void AComputePipelineSwitchAdoptsItsTableOnTheSameRule()
        {
            MetalShaderIndexTable shared = EmptyTable();

            MetalCommandList list = NewList();
            list.Begin();

            Assert.Null(list.ComputeBinds.IndexTable);

            list.SetComputePipeline(ComputePipeline(shared));
            Assert.Same(shared, list.ComputeBinds.IndexTable);

            list.ComputeBinds.Record(0, MetalBindProgram.Set(_harness), 0);
            var sink = new FakeMetalEncoderSink(new FakeMetalEncoderCalls());
            list.ComputeBinds.Flush(ref sink, new IntPtr(0xC5), list.Encoders.Epoch, segment: 0);
            Assert.False(list.ComputeBinds.IsDirty(0));

            list.SetComputePipeline(ComputePipeline(shared));
            Assert.False(list.ComputeBinds.IsDirty(0));

            MetalShaderIndexTable other = EmptyTable();
            list.SetComputePipeline(ComputePipeline(other));
            Assert.Same(other, list.ComputeBinds.IndexTable);
            Assert.True(list.ComputeBinds.IsDirty(0));

            list.End();
        }

        /// <summary>
        /// M-A6's SCISSOR GATE REACHES THE SCHEDULE ON A PIPELINE CHANGE. Metal has no scissor-test enable of its
        /// own, so the seam's flag is honoured by this backend deciding whether to emit the rectangle at all.
        /// Without this propagation the gate reads false for the whole recording and a draw rasterises the entire
        /// attachment where the caller asked for a rectangle: no crash, no validation error, a wrong frame.
        /// </summary>
        [Fact]
        public void APipelineSwitchPropagatesTheScissorGateToTheSchedule()
        {
            MetalCommandList list = NewList();
            list.Begin();

            // A fresh schedule gates the scissor OFF, which is what a recording with no pipeline bound holds.
            Assert.False(list.Passes.ScissorTestEnabled);

            list.SetPipeline(Pipeline(scissorTestEnabled: true));
            Assert.True(list.Passes.ScissorTestEnabled);

            list.SetPipeline(Pipeline(scissorTestEnabled: false));
            Assert.False(list.Passes.ScissorTestEnabled);

            list.End();
        }

        // ---- fixtures ------------------------------------------------------------------------------------

        MetalCommandList NewList()
            => _harness.NewList(new object(), new FakeMetalCommandBufferSource(), new FakeMetalEncoderCalls(),
                new MetalUncommittedBuffers(_harness.FramesInFlight, new RecordingLogger()));

        MetalGraphicsPipeline Pipeline(IMetalDeviceLiveness? owner = null, MetalShaderIndexTable? table = null,
            bool scissorTestEnabled = false)
        {
            IMetalDeviceLiveness liveness = owner ?? _harness.Liveness;
            MetalShaderSet shaders = new(
                liveness,
                [
                    new MetalCompiledStage(MetalShaderStage.Vertex, default, default),
                    new MetalCompiledStage(MetalShaderStage.Fragment, default, default),
                ],
                table ?? EmptyTable());

            var description = new GpuPipelineDescription
            {
                ShaderSet = shaders,
                ResourceLayouts = [],
                BlendAttachments = [GpuBlendAttachment.OverrideBlend],
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid,
                    GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled),
                Topology = GpuPrimitiveTopology.TriangleList,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = new GpuOutputDescription(null, GpuPixelFormat.B8G8R8A8UNorm),
            };

            // Nil handles: nothing below SetPipeline dereferences either, and the plan is the real one so the
            // pipeline this hands out is the object a device would have produced minus its two Metal objects.
            return new MetalGraphicsPipeline(
                liveness, MetalGraphicsPipelinePlan.Build(liveness, description), default, default);
        }

        MetalComputePipeline ComputePipeline(MetalShaderIndexTable? table = null)
            => new(_harness.Liveness,
                new MetalComputeShader(_harness.Liveness, default, table ?? EmptyTable(), 64, 1, 1),
                [],
                default);

        // A table over NO layouts and no entries, which is what a shader that references nothing reflects. It is
        // the shape RequireLayoutShape accepts an empty declared array against.
        static MetalShaderIndexTable EmptyTable()
            => MetalShaderIndexTable.Build([], [], "hand-built");
    }
}
