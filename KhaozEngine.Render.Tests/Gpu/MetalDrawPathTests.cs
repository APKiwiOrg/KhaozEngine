using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// WHAT A DRAW ACTUALLY EMITS, DEVICE-FREE: which calls, in which order, into which encoder, and how few of
    /// them a second draw costs. Work-breakdown row 14
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580), section 6.3 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    ///
    /// <para><b>THE DISPATCH HALF OF ROW 14 IS <see cref="MetalDispatchPathTests"/>, split off by SUBJECT rather
    /// than at whatever line the file-size cap fell.</b> A dispatch is a different encoder kind, a different
    /// Objective-C protocol and a different set of bind records, and the one thing the two subjects share (a
    /// render pass a dispatch has to end) is asserted there because that is where the dispatch is. The framebuffer
    /// stand-in below is shared, since both need one.</para>
    ///
    /// <para><b>EVERY CLAIM HERE IS A DECISION RATHER THAN A DRIVER CALL, WHICH IS WHY IT CAN BE A PLAIN
    /// <c>[Fact]</c> AND WHY IT HAS TO BE ONE.</b> A missing pre-draw step does not throw and does not report: it
    /// renders PLAUSIBLY WRONG. A state block that is emitted twice costs five calls a frame nobody counts. A
    /// state block that is NOT re-emitted after an encoder boundary draws with whatever the previous pipeline
    /// left in force. A depth trio sent to a pass with no depth attachment is a validation error only the debug
    /// layer on the one Metal leg can see, and a trio SKIPPED for a pass that has one silently leaves the
    /// previous pipeline's depth test applying. None of that is visible in a golden, and all of it is visible
    /// here.</para>
    ///
    /// <para><b>THE LIST IS THE REAL ONE, OVER THE THREE FAKE SEAMS.</b>
    /// <see cref="MetalRingHarness.NewList"/> assembles a genuine <see cref="MetalCommandList"/>, a genuine
    /// <see cref="MetalEncoderScope"/>, a genuine <see cref="MetalRenderPassSchedule"/> and genuine bind records
    /// over <see cref="FakeMetalEncoderCalls"/>, <see cref="FakeMetalRenderCalls"/> and
    /// <see cref="FakeMetalComputeApi"/>, so the relations these rows are about (which encoder a call went into,
    /// whether a boundary invalidated a record) are the shipped ones rather than a re-derivation of them.</para>
    ///
    /// <para><b>THE PIPELINES HOLD NIL HANDLES AND THE FRAMEBUFFER IS A PLAIN RECORD.</b> A
    /// <see cref="MetalGraphicsPipeline"/> is a liveness token, a resolved plan and two Objective-C handles, and
    /// only an emission or a disposal touches the handles. A <c>MetalFramebuffer</c> needs real
    /// <c>MetalTexture</c>s and therefore a device, so <see cref="RecordedFramebuffer"/> below implements the
    /// same <c>IMetalBoundFramebufferSource</c> seam the swapchain's framebuffer will, which is exactly the
    /// indirection that seam exists for.</para>
    ///
    /// <para><b>THE ONE ORDERING CLAIM THESE FAKES CANNOT WITNESS</b> is whether the pipeline-state block goes
    /// out before or after the argument-table writes, because the two land in two separate logs with nothing
    /// interleaving them. It is also the one step whose order is not load-bearing: an encoder takes its state in
    /// any order before the draw, and reproducing the incumbent's sequence buys parity rather than correctness.
    /// Everything the order rows below assert is a position one of the two logs really does record.</para>
    /// </summary>
    public sealed class MetalDrawPathTests : IDisposable
    {
        /// <inheritdoc/>
        public void Dispose()
        {
            foreach (MetalCommandList list in _lists) list.Dispose();
            _harness.Dispose();
        }

        // ONE REAL RING SUBSYSTEM for the whole fixture, because Begin is this backend's frame boundary and needs
        // a real allocator behind it (M-R2). Every list is disposed with the fixture rather than per test, since
        // a list owns its staging arena and dropping one would leak the arena's blocks.
        readonly MetalRingHarness _harness = new();
        readonly List<MetalCommandList> _lists = new();

        // THE REAL INDEX TABLE, READ OUT OF A REAL MSL EMISSION by the shipped path (MetalShaderBuild), built
        // ONCE for the assembly because the cross-compile is the expensive part of every row that binds. Its
        // vertex stage reads binding 0 alone and its fragment stage reads all four, which is the partial-stage
        // shape MetalBindProgram exists for and the reason the bind flush emits four array calls rather than one.
        static readonly MetalShaderIndexTable GraphicsTable = MetalBindProgram.Table();

        // ---- The order (section 6.3's four steps) --------------------------------------------------------

        /// <summary>
        /// THE HEADLINE: ONE DRAW EMITS THE PASS, THE VIEWPORT, THE STATE BLOCK, THE ARGUMENT TABLES AND THEN THE
        /// COMMAND, ALL INTO ONE ENCODER. Five entry points repeat the fork that chooses a sink and exactly one
        /// place writes this order, so the row that guards it has to read the order back rather than count the
        /// calls.
        ///
        /// <para><b>WHICH CLAIM EACH ASSERTION CARRIES.</b>
        /// (1) NOTHING is emitted by the framebuffer bind, the pipeline bind or either resource bind, which is
        /// M-A1's deferred begin: a bind that opened an encoder would make every clear after it cost a boundary.
        /// (2) The draw emits exactly one of each, so no step ran twice.
        /// (3) In the render seam's own log the pass descriptor comes first, its release marks the point the
        /// encoder was opened, and the viewport and then the state block follow, which is the encoder existing
        /// before anything is set on it.
        /// (4) In the encoder seam's own log the begin comes first, the argument-table writes follow and the DRAW
        /// IS LAST, which is the step order that renders wrong rather than throwing when it slips.
        /// (5) Every one of them names the ONE encoder the begin handed back, which is what a step targeting a
        /// stale handle would fail.</para>
        ///
        /// <para><b>WHAT A RED RUN MEANS.</b> A step moved, a step went missing, or a step reached a different
        /// encoder from the draw that depends on it. All three render a wrong frame with nothing reported.</para>
        /// </summary>
        [Fact]
        public void ADrawEmitsThePassTheViewportTheStateBlockTheBindsThenTheCommand()
        {
            (MetalCommandList list, FakeMetalEncoderCalls calls, FakeMetalRenderCalls render, _) = NewList();

            list.Begin();
            list.SetFramebuffer(new RecordedFramebuffer());
            list.SetPipeline(Pipeline());
            list.GraphicsBinds.Record(0, MetalBindProgram.Set(_harness), 0);
            list.SetVertexBuffer(0, _harness.NewBuffer(256, GpuBufferUsage.VertexBuffer));

            // (1) The deferred begin: four record-time calls and not one emission.
            Assert.Empty(render.Log);
            Assert.Empty(calls.Log);

            list.Draw(3);

            // (2) One of each.
            Assert.Single(render.Passes);
            Assert.Single(render.Viewports);
            Assert.Single(render.StateBlocks);
            Assert.Single(calls.Draws);
            Assert.NotEmpty(calls.ArrayWrites);

            // (3) The render seam's order. The release sits immediately after the open, because the schedule
            // releases the descriptor in a finally around EnsureRenderEncoder.
            int descriptor = At(render.Log, "descriptor ");
            int released = At(render.Log, "release descriptor ");
            int viewport = At(render.Log, "viewport ");
            int stateBlock = At(render.Log, "state block ");

            Assert.True(descriptor >= 0 && descriptor < released,
                "the pass descriptor is built before the encoder it opens");
            Assert.True(released < viewport, "the viewport is set on an encoder that already exists");
            Assert.True(viewport < stateBlock, "the pipeline-state block follows the dynamic state");

            // (4) The encoder seam's order, and the draw is the LAST thing that reached it.
            int begin = At(calls.Log, "begin Render");
            int firstWrite = At(calls.Log, "buffers[");
            int draw = At(calls.Log, "draw ");

            Assert.True(begin >= 0 && begin < firstWrite, "the argument tables are written after the begin");
            Assert.True(firstWrite < draw, "the draw follows every argument-table write it depends on");
            Assert.Equal(calls.Log.Count - 1, draw);

            // (5) One encoder, and everything named it.
            IntPtr encoder = Assert.Single(calls.RetainedEncoders);
            Assert.Equal(encoder, render.Viewports[0].Encoder);
            Assert.Equal(encoder, render.StateBlocks[0].Encoder);
            Assert.Equal(encoder, calls.Draws[0].Encoder);
            Assert.All(calls.ArrayWrites, write => Assert.Equal(encoder, write.Encoder));

            list.End();
        }

        // ---- The state block, once per (pipeline, encoder) -----------------------------------------------

        /// <summary>
        /// THE STATE BLOCK IS EMITTED ONCE PER PIPELINE PER ENCODER, WHICH IS M-R8 AND M-R4 MEETING. A second
        /// draw with nothing changed emits none, a REDUNDANT bind of the pipeline already in place emits none
        /// (the guard the incumbent lacked, whose <c>SetPipelineCore</c> set its changed flag on every call), and
        /// a genuinely different pipeline emits one.
        ///
        /// <para><b>WHAT A RED RUN MEANS.</b> Too many blocks is a cost nothing else can see: five encoder calls
        /// per redundant bind, plus a full re-activation of every resource set on the incumbent's shape. Too few
        /// is worse and is the next row: a draw running under another pipeline's state.</para>
        /// </summary>
        [Fact]
        public void TheStateBlockIsEmittedOncePerPipelinePerEncoder()
        {
            (MetalCommandList list, FakeMetalEncoderCalls calls, FakeMetalRenderCalls render, _) = NewList();
            MetalGraphicsPipeline first = Pipeline();

            list.Begin();
            list.SetFramebuffer(new RecordedFramebuffer());
            list.SetPipeline(first);

            list.Draw(3);
            Assert.Single(render.StateBlocks);

            // Nothing changed at all.
            list.Draw(3);
            Assert.Single(render.StateBlocks);

            // M-R8: the same pipeline again reports no change, so the caller does none of the work a switch owes
            // and the stamp survives.
            list.SetPipeline(first);
            list.Draw(3);
            Assert.Single(render.StateBlocks);

            // A genuine change puts the block back, without which the guard above is satisfied by one that never
            // lets anything through.
            list.SetPipeline(Pipeline());
            list.Draw(3);
            Assert.Equal(2, render.StateBlocks.Count);

            // ALL FOUR DRAWS SHARED ONE PASS, so the second block is the pipeline change and not a boundary.
            Assert.Equal(4, calls.Draws.Count);
            Assert.Equal(1, calls.EncoderBegins);

            list.End();
        }

        /// <summary>
        /// AND IT IS RE-EMITTED AFTER AN ENCODER BOUNDARY (M-R4), ALONG WITH THE VIEWPORT AND EVERY BIND. The
        /// boundary here is the ordinary one 2.1 is about: a record-time <c>UpdateBuffer</c> to a NON-uniform
        /// buffer takes the staging path, which opens a BLIT encoder and therefore ends the render encoder
        /// underneath the pass, with nothing in the schedule being told.
        ///
        /// <para><b>THIS IS THE ROW THAT WOULD BE GREEN ON A GOLDEN AND WRONG IN A GAME.</b> The goldens do not
        /// restart a render pass mid-scene, so a backend that kept its records across the boundary renders every
        /// committed frame correctly and corrupts the first shipped scene that uploads a mesh mid-pass. The
        /// incumbent has exactly that gap in its vertex-stream cache and is saved only by a second defect that
        /// keeps the cache permanently cold.</para>
        ///
        /// <para><b>WHAT A RED RUN MEANS.</b> The second draw is running against a fresh encoder whose pipeline
        /// state, viewport and argument tables are all empty, so a missing re-emission is a draw with no pipeline
        /// state at all or one reading resources nothing wrote.</para>
        /// </summary>
        [Fact]
        public void AnEncoderBoundaryReEmitsTheStateBlockTheViewportAndEveryBind()
        {
            (MetalCommandList list, FakeMetalEncoderCalls calls, FakeMetalRenderCalls render, _) = NewList();

            list.Begin();
            list.SetFramebuffer(new RecordedFramebuffer());
            list.SetPipeline(Pipeline());
            list.GraphicsBinds.Record(0, MetalBindProgram.Set(_harness), 0);
            list.SetVertexBuffer(0, _harness.NewBuffer(256, GpuBufferUsage.VertexBuffer));

            list.Draw(3);
            int firstBatch = calls.ArrayWrites.Count;
            IntPtr firstEncoder = calls.Draws[0].Encoder;

            // THE BOUNDARY. A bulk write is not ring-backed, so it stages and copies, and opening the blit
            // encoder ends the render encoder.
            list.UpdateBuffer(
                _harness.NewBuffer(256, GpuBufferUsage.VertexBuffer), 0, new ReadOnlySpan<byte>(new byte[16]));
            Assert.Equal(MetalEncoderKind.Blit, list.Encoders.Open);

            list.Draw(3);

            // A SECOND PASS, and a fresh block and viewport in it.
            Assert.Equal(2, render.Passes.Count);
            Assert.Equal(2, render.StateBlocks.Count);
            Assert.Equal(2, render.Viewports.Count);

            IntPtr secondEncoder = calls.Draws[1].Encoder;
            Assert.NotEqual(firstEncoder, secondEncoder);
            Assert.Equal(secondEncoder, render.StateBlocks[1].Encoder);
            Assert.Equal(secondEncoder, render.Viewports[1].Encoder);

            // AND EVERY ARGUMENT-TABLE ENTRY AND VERTEX STREAM AGAIN, at the same indices and the same widths,
            // into the new encoder. The counts alone would pass for a rebind that covered a different run.
            Assert.Equal(firstBatch * 2, calls.ArrayWrites.Count);
            Assert.Equal(Shape(calls.ArrayWrites.Take(firstBatch)), Shape(calls.ArrayWrites.Skip(firstBatch)));
            Assert.All(calls.ArrayWrites.Skip(firstBatch), w => Assert.Equal(secondEncoder, w.Encoder));

            // AND NOT THE OFFSETS-ONLY ARM, which is the sharp half: setBufferOffset: against an index holding no
            // buffer at all is undefined, so the boundary has to beat that comparison.
            Assert.Empty(calls.OffsetWrites);

            list.End();
        }

        // ---- The depth trio -------------------------------------------------------------------------------

        /// <summary>
        /// THE DEPTH-TRIO GUARD, BOTH WAYS, AND IT IS THE BOUND FRAMEBUFFER'S CONDITION AND NOTHING ELSE.
        ///
        /// <para><b>THIS ROW EXISTS BECAUSE THE DEBUG LAYER WOULD OTHERWISE BE THE ONLY WITNESS, AND ONLY ON ONE
        /// LEG.</b> Sending <c>-setDepthStencilState:</c> and its two companions to a pass with no depth
        /// attachment is a validation error under <c>MTL_DEBUG_LAYER</c>, which M-T7 arms on the native Metal leg
        /// alone. The other direction reports NOTHING anywhere: skipping the trio for a pass that HAS depth
        /// leaves whatever the previous pipeline set in force, which is a depth test that silently keeps
        /// applying, and the 36 committed <c>metal</c> goldens were baked through the incumbent emitting it.</para>
        ///
        /// <para><b>THE GUARD IS NOT THE ONE A READER EXPECTS, WHICH IS WHY BOTH ARMS USE THE SAME PIPELINE.</b>
        /// Two things could plausibly gate the trio: the framebuffer having a depth attachment, or the pipeline
        /// declaring a depth output. The incumbent asked only the first, so the two blocks below differ in exactly
        /// one field and the equality assertion pins that.</para>
        ///
        /// <para><b>WHAT A RED RUN MEANS.</b> Either a colour-only pass is being sent the trio (a validation
        /// error the Metal leg reports and the other four legs do not), or a depth pass is not, which is a
        /// stale depth test and a wrong picture with nothing said.</para>
        /// </summary>
        [Fact]
        public void TheDepthTrioIsTheBoundFramebuffersConditionAndNothingElse()
        {
            (MetalCommandList list, _, FakeMetalRenderCalls render, _) = NewList();
            MetalGraphicsPipeline pipeline = Pipeline();

            list.Begin();
            list.SetFramebuffer(new RecordedFramebuffer(depth: true));
            list.SetPipeline(pipeline);
            list.Draw(3);

            MetalGraphicsStateBlock withDepth = render.StateBlocks[0].Block;
            Assert.True(withDepth.DepthTrio);
            Assert.Equal(MetalGraphicsStateBlock.For(pipeline, framebufferHasDepth: true), withDepth);

            // The framebuffer change ends the pass, so the next draw opens a new encoder and owes a fresh block.
            list.SetFramebuffer(new RecordedFramebuffer(depth: false));
            list.Draw(3);

            MetalGraphicsStateBlock withoutDepth = render.StateBlocks[1].Block;
            Assert.False(withoutDepth.DepthTrio);

            // ONE FIELD APART, on one pipeline, which is the whole of the claim that the guard is the
            // framebuffer's.
            Assert.Equal(2, render.StateBlocks.Count);
            Assert.Equal(withDepth with { DepthTrio = false }, withoutDepth);

            list.End();
        }

        // ---- The nil encoder (M-W5) -----------------------------------------------------------------------

        /// <summary>
        /// M-W5's ORPHAN TARGET: A NIL RENDER ENCODER EMITS NOTHING AND LEAVES EVERY RECORD DIRTY. A framebuffer
        /// whose drawable came back nil is a legitimate runtime state, and the seam's answer is that the frame's
        /// draws go nowhere while the frame still COUNTS.
        ///
        /// <para><b>THE SECOND HALF IS THE LOAD-BEARING ONE.</b> A message to nil is a silent no-op in
        /// Objective-C, so a state block and a bind flush into a nil encoder would go nowhere while every record
        /// was marked CLEAN, and a LATER frame would then render against argument tables nothing ever wrote. That
        /// is a corruption one frame removed from its cause, which is why the dirty assertions below matter more
        /// than the empty ones.</para>
        ///
        /// <para><b>WHAT A RED RUN MEANS.</b> Either the early return moved below a step (a call into nil, or a
        /// flush that throws inside a frame that is already failing), or the records were cleaned against an
        /// encoder that never existed.</para>
        /// </summary>
        [Fact]
        public void ANilRenderEncoderEmitsNothingAndLeavesEveryRecordDirty()
        {
            (MetalCommandList list, FakeMetalEncoderCalls calls, FakeMetalRenderCalls render, _) = NewList();
            calls.NilForKind = MetalEncoderKind.Render;

            list.Begin();
            list.SetFramebuffer(new RecordedFramebuffer());
            list.SetPipeline(Pipeline());
            list.GraphicsBinds.Record(0, MetalBindProgram.Set(_harness), 0);
            list.SetVertexBuffer(0, _harness.NewBuffer(256, GpuBufferUsage.VertexBuffer));

            list.Draw(3);

            // Nothing reached anything. The descriptor was still built and still released, which is the
            // ownership rule holding on the path where the encoder never arrived.
            Assert.Empty(render.StateBlocks);
            Assert.Empty(render.Viewports);
            Assert.Empty(calls.ArrayWrites);
            Assert.Empty(calls.Draws);
            Assert.Equal(0, render.OutstandingDescriptors);
            Assert.Equal(0, calls.OutstandingEncoders);

            // AND EVERY RECORD STILL OWES ITS BIND, so the next frame that gets a drawable writes the tables
            // rather than believing they were written.
            Assert.True(list.GraphicsBinds.IsDirty(0));
            Assert.True(list.VertexStreams.IsDirty(0));
            Assert.True(list.Passes.ViewportOwed);
            Assert.True(list.Pipelines.NeedsGraphicsStateBlock(list.Encoders.Epoch));

            list.End();
        }

        // ---- The command arguments ------------------------------------------------------------------------

        /// <summary>
        /// THE TOPOLOGY IS THE BOUND PIPELINE'S RESOLVED PRIMITIVE TYPE, AND IT TRAVELS PER DRAW. This is the one
        /// place the three backends genuinely differ: Direct3D 11 sets it on the input assembler and Vulkan bakes
        /// it into the pipeline, where Metal takes it as a DRAW argument. Row 11 resolves it once at pipeline
        /// creation, so nothing is mapped per draw and the only way it can be wrong is by naming the wrong
        /// pipeline.
        ///
        /// <para><b>WHAT A RED RUN MEANS.</b> A draw is assembling primitives the pipeline was not built for,
        /// which rasterises geometry rather than failing.</para>
        /// </summary>
        [Fact]
        public void TheTopologyIsTheBoundPipelinesAndItChangesWithThePipeline()
        {
            (MetalCommandList list, FakeMetalEncoderCalls calls, _, _) = NewList();

            list.Begin();
            list.SetFramebuffer(new RecordedFramebuffer());
            list.SetPipeline(Pipeline());
            list.Draw(3);

            list.SetPipeline(Pipeline(GpuPrimitiveTopology.LineStrip));
            list.Draw(4, 2, 1, 5);

            Assert.Equal(MTLPrimitiveType.Triangle, calls.Draws[0].Call.Topology);
            Assert.Equal(MTLPrimitiveType.LineStrip, calls.Draws[1].Call.Topology);

            // AND THE FOUR COUNTS ARRIVE UNTOUCHED, including the two that cross on the stack.
            FakeMetalDrawCall second = calls.Draws[1].Call;
            Assert.Equal(4u, second.VertexCount);
            Assert.Equal(2u, second.InstanceCount);
            Assert.Equal(1u, second.VertexStart);
            Assert.Equal(5u, second.BaseInstance);

            // The convenience overload is the full one at instanceCount 1 with both starts at zero.
            FakeMetalDrawCall first = calls.Draws[0].Call;
            Assert.Equal(new FakeMetalDrawCall(MTLPrimitiveType.Triangle, 0, 3, 1, 0), first);

            list.End();
        }

        /// <summary>
        /// DRAWINDEXED'S ARITHMETIC REACHES THE SEAM: the byte offset is the element width times the element
        /// start, and the base vertex is the caller's SIGNED value, negative included.
        ///
        /// <para><b>BOTH NUMBERS DRAW A DIFFERENT MESH WHEN THEY ARE WRONG, WITH NOTHING REPORTED.</b> The offset
        /// picks which run of indices is read out of a shared index buffer. The base vertex is added to every
        /// index before the vertex buffer is read, and a mesh packed behind another one in a shared buffer passes
        /// a NEGATIVE one, which is also the value most easily lost to an unsigned parameter somewhere on the
        /// way down.</para>
        /// </summary>
        [Fact]
        public void DrawIndexedCarriesTheByteOffsetAndTheSignedBaseVertex()
        {
            (MetalCommandList list, FakeMetalEncoderCalls calls, _, _) = NewList();
            MetalBuffer indices = _harness.NewBuffer(256, GpuBufferUsage.IndexBuffer);

            list.Begin();
            list.SetFramebuffer(new RecordedFramebuffer());
            list.SetPipeline(Pipeline());

            list.SetIndexBuffer(indices, GpuIndexFormat.UInt32);
            list.DrawIndexed(indexCount: 9, instanceCount: 2, indexStart: 7, vertexOffset: -12, instanceStart: 3);

            FakeMetalIndexedDrawCall wide = calls.IndexedDraws[0].Call;
            Assert.Equal((nuint)28, wide.IndexBufferOffset);
            Assert.Equal(-12, wide.BaseVertex);
            Assert.False(wide.SixteenBitIndices);
            Assert.Equal(9u, wide.IndexCount);
            Assert.Equal(2u, wide.InstanceCount);
            Assert.Equal(3u, wide.BaseInstance);
            Assert.Equal(indices.Handle.Handle, wide.IndexBuffer);
            Assert.Equal(MTLPrimitiveType.Triangle, wide.Topology);

            // THE NARROW WIDTH, so the row says "times the element width" rather than "times four".
            list.SetIndexBuffer(indices, GpuIndexFormat.UInt16);
            list.DrawIndexed(indexCount: 9, instanceCount: 1, indexStart: 7, vertexOffset: 0, instanceStart: 0);

            FakeMetalIndexedDrawCall narrow = calls.IndexedDraws[1].Call;
            Assert.Equal((nuint)14, narrow.IndexBufferOffset);
            Assert.True(narrow.SixteenBitIndices);

            list.End();
        }

        /// <summary>
        /// AN INDEXED DRAW WITH NO INDEX BUFFER IS REFUSED, AND IT IS REFUSED EVEN WHEN THE ENCODER CAME BACK
        /// NIL. The refusal comes before the pass opens at all, which puts it before M-W5's orphan arm for free:
        /// a recording that forgot <c>SetIndexBuffer</c> has made a caller error whether or not this frame's
        /// drawable arrived, so the orphan arm must not SWALLOW it. A frame that silently dropped the mistake
        /// would report it on the next frame that happened to get a drawable, which is a bug report about the
        /// wrong frame.
        ///
        /// <para><b>WHAT A RED RUN MEANS.</b> Either the refusal moved below the nil arm (an intermittent
        /// exception that names the wrong frame), or it went away entirely, in which case the call names a nil
        /// <c>MTLBuffer</c> and Metal aborts the process from inside the driver.</para>
        /// </summary>
        [Fact]
        public void DrawIndexedWithNoIndexBufferIsRefused_EvenWhenTheEncoderCameBackNil()
        {
            (MetalCommandList live, _, _, _) = NewList();
            live.Begin();
            live.SetFramebuffer(new RecordedFramebuffer());
            live.SetPipeline(Pipeline());

            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
                () => live.DrawIndexed(3, 1, 0, 0, 0));
            Assert.Contains("with no index buffer bound", refused.Message, StringComparison.Ordinal);
            live.End();

            // THE SAME REFUSAL ON A FRAME WHOSE ENCODER NEVER ARRIVED, which is the half the ordering exists for.
            (MetalCommandList orphan, FakeMetalEncoderCalls calls, _, _) = NewList();
            calls.NilForKind = MetalEncoderKind.Render;

            orphan.Begin();
            orphan.SetFramebuffer(new RecordedFramebuffer());
            orphan.SetPipeline(Pipeline());

            InvalidOperationException stillRefused = Assert.Throws<InvalidOperationException>(
                () => orphan.DrawIndexed(3, 1, 0, 0, 0));
            Assert.Contains("with no index buffer bound", stillRefused.Message, StringComparison.Ordinal);

            // AND THE PLAIN DRAW ON THAT SAME FRAME IS SILENT, so the refusal above is about the missing index
            // buffer rather than about the nil encoder.
            orphan.Draw(3);
            Assert.Empty(calls.Draws);
            orphan.End();
        }

        // ---- The refusals ---------------------------------------------------------------------------------

        /// <summary>
        /// AND THAT REFUSAL SPENDS NOTHING, WHICH INCLUDES THE PENDING CLEARS. The branch's rule is that a refusal
        /// costs no encoder boundary, and this member was the one that broke it: <c>PrepareDraw</c> ran first, so
        /// the pass opened and CONSUMED the clears into its load actions before the index refusal was reached, and
        /// the recording lost them to a draw that never happened.
        ///
        /// <para><b>THE CLEAR IS THE HALF A BOUNDARY COUNT CANNOT SEE.</b> A pass that opened and threw leaves the
        /// clear folded into a descriptor nobody drew into, so the next real draw opens a pass with
        /// <c>loadAction = Load</c> and the frame renders over whatever the target held. Asserting the clear is
        /// still owed means driving the next draw and reading its load action, which is what this does.</para>
        /// </summary>
        [Fact]
        public void DrawIndexedWithNoIndexBufferSpendsNothing_TheClearsIncluded()
        {
            (MetalCommandList list, FakeMetalEncoderCalls calls, FakeMetalRenderCalls render, _) = NewList();
            Color owed = new(0.25f, 0.5f, 0.75f, 1f);

            list.Begin();
            list.SetFramebuffer(new RecordedFramebuffer());
            list.SetPipeline(Pipeline());
            list.ClearColorTarget(0, owed);

            Assert.Throws<InvalidOperationException>(() => list.DrawIndexed(3, 1, 0, 0, 0));

            Assert.Empty(render.Passes);
            Assert.Equal(0, calls.EncoderBoundaries);
            Assert.Equal(MetalEncoderKind.None, list.Encoders.Open);

            // AND THE CLEAR IS STILL OWED: the next draw, with the index buffer the refusal was about, folds it
            // into the pass it opens.
            list.SetIndexBuffer(_harness.NewBuffer(64, GpuBufferUsage.IndexBuffer), GpuIndexFormat.UInt16);
            list.DrawIndexed(3, 1, 0, 0, 0);

            Assert.Single(render.Passes);
            Assert.Equal(owed, render.ClearOn(0));

            list.End();
        }

        /// <summary>
        /// A DRAW WITH NO GRAPHICS PIPELINE IS REFUSED BEFORE THE PASS OPENS, which is why <c>BeginDraw</c> reads
        /// the pipeline first: a recording with no pipeline is refused without having spent an encoder on it, so
        /// the mis-sequenced frame does not also pay a boundary and leave a pass open behind the throw.
        /// </summary>
        [Fact]
        public void ADrawWithNoGraphicsPipelineIsRefusedBeforeThePassOpens()
        {
            (MetalCommandList list, FakeMetalEncoderCalls calls, FakeMetalRenderCalls render, _) = NewList();

            list.Begin();
            list.SetFramebuffer(new RecordedFramebuffer());

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => list.Draw(3));
            Assert.Contains("no graphics pipeline bound", thrown.Message, StringComparison.Ordinal);

            Assert.Empty(render.Passes);
            Assert.Equal(0, calls.EncoderBoundaries);
            Assert.Equal(MetalEncoderKind.None, list.Encoders.Open);

            list.End();
        }

        /// <summary>
        /// EACH OF THE THREE COMMANDS REFUSES A LIST THAT IS NOT RECORDING WITH ITS OWN MESSAGE, which is the
        /// difference between "call Begin" and every other reason a draw can fail. The three prefixes are
        /// asserted whole rather than as a shared substring, because "Drawing" is a prefix of "Drawing indexed"
        /// and a test written on the short one passes on a backend that answers one message for all three.
        /// </summary>
        [Fact]
        public void TheThreeCommandsRefuseAListThatIsNotRecording()
        {
            (MetalCommandList list, FakeMetalEncoderCalls calls, FakeMetalRenderCalls render, _) = NewList();

            Assert.StartsWith("Drawing needs",
                Assert.Throws<InvalidOperationException>(() => list.Draw(3)).Message, StringComparison.Ordinal);
            Assert.StartsWith("Drawing needs",
                Assert.Throws<InvalidOperationException>(() => list.Draw(3, 1, 0, 0)).Message,
                StringComparison.Ordinal);
            Assert.StartsWith("Drawing indexed needs",
                Assert.Throws<InvalidOperationException>(() => list.DrawIndexed(3, 1, 0, 0, 0)).Message,
                StringComparison.Ordinal);
            Assert.StartsWith("Dispatching compute work needs",
                Assert.Throws<InvalidOperationException>(() => list.Dispatch(1, 1, 1)).Message,
                StringComparison.Ordinal);

            Assert.Empty(calls.Log);
            Assert.Empty(render.Log);
        }

        // ---- The budget (M-T2, section 6.3) ---------------------------------------------------------------

        /// <summary>
        /// THE PER-DRAW MARGINAL, FROZEN: a second identical draw after a full activation costs EXACTLY ONE
        /// native call through the sink, the draw itself. Zero argument-table writes, zero vertex-stream writes,
        /// zero pipeline-state blocks, zero encoder boundaries.
        ///
        /// <para><b>THIS IS A REGRESSION TARGET RATHER THAN A PARITY TARGET, and the difference matters.</b> The
        /// incumbent pays one <c>setVertexBuffer</c> per stream per draw UNCONDITIONALLY, because its
        /// <c>PreDrawCommand</c> issues the bind when its cache flag is false and never sets it true, so the
        /// cache is permanently cold. The native marginal is therefore strictly LOWER than the incumbent's and
        /// this number is frozen at the lower one on purpose. A future change that reintroduces the
        /// unconditional bind is then a RED TEST rather than an invisible cost, which is the entire reason to
        /// freeze a number at all.</para>
        ///
        /// <para><b>WHAT A RED RUN MEANS.</b> The per-draw cost moved. Nothing about a frame looks different, and
        /// on a scene with thousands of draws the difference is the whole of what the caches were built to
        /// buy.</para>
        /// </summary>
        [Fact]
        public void ASecondIdenticalDrawCostsExactlyOneNativeCall()
        {
            (MetalCommandList list, FakeMetalEncoderCalls calls, FakeMetalRenderCalls render, _) = NewList();

            list.Begin();
            list.SetFramebuffer(new RecordedFramebuffer());
            list.SetPipeline(Pipeline());
            list.GraphicsBinds.Record(0, MetalBindProgram.Set(_harness), 0);
            list.SetVertexBuffer(0, _harness.NewBuffer(256, GpuBufferUsage.VertexBuffer));

            list.Draw(3);

            int tables = calls.ArgumentTableWrites;
            int boundaries = calls.EncoderBoundaries;
            int commands = calls.DrawsAndDispatches;
            int blocks = render.StateBlocks.Count;
            int viewports = render.Viewports.Count;

            list.Draw(3);

            // The three classes M-T2 counts, one at a time, so a red run names which one moved.
            Assert.Equal(tables, calls.ArgumentTableWrites);
            Assert.Equal(boundaries, calls.EncoderBoundaries);
            Assert.Equal(commands + 1, calls.DrawsAndDispatches);

            // And the two uncounted emission seams, which scale with passes rather than with draws.
            Assert.Equal(blocks, render.StateBlocks.Count);
            Assert.Equal(viewports, render.Viewports.Count);

            // THE NUMBER ITSELF, as one assertion, which is the thing that is frozen.
            Assert.Equal(
                tables + boundaries + commands + 1,
                calls.ArgumentTableWrites + calls.EncoderBoundaries + calls.DrawsAndDispatches);

            list.End();
        }

        // ---- fixtures --------------------------------------------------------------------------------------

        // THE POSITION OF A LOG LINE, so a row can assert an ORDER rather than a count. Answers -1 for a line
        // that is not there, which fails the comparisons below rather than passing them.
        static int At(IReadOnlyList<string> log, string prefix)
        {
            for (int i = 0; i < log.Count; i++)
            {
                if (log[i].StartsWith(prefix, StringComparison.Ordinal)) return i;
            }

            return -1;
        }

        // WHICH RUN OF WHICH TABLE EACH ARRAY CALL COVERED, which is what a re-bind after a boundary has to
        // reproduce. The handles are deliberately not compared: the harness fabricates one buffer handle for
        // every buffer, so comparing them would assert nothing.
        static (MetalShaderStage Stage, MetalIndexSpace Space, uint FirstIndex, int Count)[] Shape(
            IEnumerable<FakeMetalArrayWrite> writes)
            => writes.Select(w => (w.Stage, w.Space, w.FirstIndex, w.Objects.Length)).ToArray();

        (MetalCommandList List, FakeMetalEncoderCalls Calls, FakeMetalRenderCalls Render,
            FakeMetalComputeApi Compute) NewList()
        {
            FakeMetalEncoderCalls calls = new();
            FakeMetalRenderCalls render = new();
            FakeMetalComputeApi compute = new();

            MetalCommandList list = _harness.NewList(
                new object(), calls: calls, render: render, compute: compute);

            _lists.Add(list);
            return (list, calls, render, compute);
        }

        // A REAL PIPELINE WITH NIL HANDLES, carrying the real index table and the layouts that table itself
        // reflected, so pin 4's shape check passes on the shipped path rather than being routed around.
        MetalGraphicsPipeline Pipeline(GpuPrimitiveTopology topology = GpuPrimitiveTopology.TriangleList)
        {
            MetalShaderSet shaders = new(
                _harness.Liveness,
                [
                    new MetalCompiledStage(MetalShaderStage.Vertex, default, default),
                    new MetalCompiledStage(MetalShaderStage.Fragment, default, default),
                ],
                GraphicsTable);

            var layouts = new IGpuResourceLayout[GraphicsTable.Layouts.Count];
            for (int i = 0; i < layouts.Length; i++)
            {
                layouts[i] = new MetalResourceLayout(_harness.Liveness, GraphicsTable.Layouts[i]);
            }

            var description = new GpuPipelineDescription
            {
                ShaderSet = shaders,
                ResourceLayouts = layouts,
                BlendAttachments = [GpuBlendAttachment.OverrideBlend],
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid,
                    GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                Topology = topology,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = new GpuOutputDescription(null, GpuPixelFormat.B8G8R8A8UNorm),
            };

            return new MetalGraphicsPipeline(
                _harness.Liveness, MetalGraphicsPipelinePlan.Build(_harness.Liveness, description),
                default, default);
        }
    }

    /// <summary>
    /// A FRAMEBUFFER THE RECORDER CAN BIND, WITH NO DEVICE UNDER IT. The real <c>MetalFramebuffer</c> is built
    /// from <c>MetalTexture</c>s and a texture needs an <c>MTLDevice</c>, so the draw rows reach the schedule
    /// through the same <c>IMetalBoundFramebufferSource</c> seam row 15's swapchain framebuffer will, which is
    /// the indirection that seam was added for.
    /// <para>
    /// EACH INSTANCE MINTS ITS OWN <c>Id</c>, which is what M-A6's framebuffer-change guard compares. Two
    /// instances that shared a number would read as the same framebuffer and a rebind between them would do
    /// nothing at all, which is precisely the case the depth-trio row drives.
    /// </para>
    /// </summary>
    internal sealed class RecordedFramebuffer : IGpuFramebuffer, IMetalBoundFramebufferSource
    {
        // Pre-incremented from zero so the first is 1 and 0 stays free to mean "nothing bound", which is what a
        // fresh recording holds. Interlocked because xUnit runs test classes in parallel.
        static int _nextId;

        /// <param name="depth">Whether this framebuffer declares a depth attachment, which is the whole of the
        /// depth trio's guard.</param>
        /// <param name="width">Width in pixels, which is also the viewport and the full scissor.</param>
        /// <param name="height">Height in pixels.</param>
        internal RecordedFramebuffer(bool depth = false, uint width = 64, uint height = 64)
        {
            int id = Interlocked.Increment(ref _nextId);

            MetalAttachment[] colour =
                [new MetalAttachment(new IntPtr(0x2000 + id), GpuPixelFormat.B8G8R8A8UNorm)];

            MetalAttachment depthAttachment = depth
                ? new MetalAttachment(new IntPtr(0x30000 + id), GpuPixelFormat.R32Float)
                : default;

            Width = width;
            Height = height;
            Outputs = new GpuOutputDescription(depth ? GpuPixelFormat.R32Float : null,
                GpuPixelFormat.B8G8R8A8UNorm);

            AsBound = new MetalBoundFramebuffer(
                (ulong)id, width, height, colour, depthAttachment, DepthHasStencil: false);
        }

        /// <inheritdoc/>
        public GpuOutputDescription Outputs { get; }

        /// <inheritdoc/>
        public uint Width { get; }

        /// <inheritdoc/>
        public uint Height { get; }

        /// <summary>Everything a bind needs from this framebuffer, as plain data.</summary>
        internal MetalBoundFramebuffer AsBound { get; }

        /// <inheritdoc/>
        MetalBoundFramebuffer IMetalBoundFramebufferSource.AsBound => AsBound;

        /// <inheritdoc/>
        /// <remarks>Never the swapchain's: nothing here reaches a drawable.</remarks>
        bool IMetalBoundFramebufferSource.IsSwapchain => false;

        /// <inheritdoc/>
        /// <remarks>Releases nothing, exactly as the real framebuffer's does, because nothing native was
        /// made.</remarks>
        public void Dispose()
        {
        }
    }
}
