using System;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE DRAW AND DISPATCH FAMILY: the vertex and index binds, both <c>Draw</c> overloads, <c>DrawIndexed</c>
    /// and <c>Dispatch</c>. Work-breakdown row 14 (https://github.com/APKiwiOrg/KhaozEngine/issues/580), and the
    /// row that makes the 36 committed <c>metal</c> goldens runnable against this backend at all.
    ///
    /// <para><b>THE ORDER INSIDE A DRAW IS FOUR STEPS AND IT IS WRITTEN ONCE.</b>
    /// <see cref="MetalRenderPassSchedule.PrepareDraw"/> first, which opens the pass under M-A1's deferred begin,
    /// folds the pending clears into load actions, emits the viewport and the scissor if either is owed, and
    /// hands back the encoder. Then the PIPELINE-STATE BLOCK, when
    /// <see cref="MetalPipelineBinding.NeedsGraphicsStateBlock"/> says it has not reached this encoder. Then row
    /// 13's two flushes, the resource sets and the vertex streams. Then the command. Five entry points repeating
    /// that sequence would be five places for a step to go missing, and a missing step renders PLAUSIBLY WRONG
    /// rather than throwing, so the order lives in <see cref="PrepareGraphics"/> and nowhere else. The Vulkan
    /// sibling's row 15 took the same shape for the same reason.</para>
    ///
    /// <para><b>THE STATE BLOCK GOES BETWEEN THE PASS AND THE BINDS, WHICH IS THE INCUMBENT'S OWN ORDER.</b>
    /// <c>PreDrawCommand</c> emits the viewport, the scissor, the state block, then the resource sets, then the
    /// vertex buffers. Nothing about it is load-bearing to Metal (an encoder takes its state in any order before
    /// the draw), and it is reproduced anyway, because the only thing a divergence here could buy is a different
    /// answer to a question nobody asked.</para>
    ///
    /// <para><b>A NIL ENCODER RETURNS EARLY, BEFORE ANY OF IT (M-W5).</b> A framebuffer whose drawable came back
    /// nil is a legitimate runtime state, and the seam's answer is that the frame's draws go nowhere while the
    /// frame still COUNTS. Returning early is not a convenience: a message to nil is a silent no-op in
    /// Objective-C, so a state block and a bind flush into a nil encoder would go nowhere while every record was
    /// marked CLEAN, and a later frame would render against argument tables nothing ever wrote. Row 13's two
    /// flushes refuse a nil encoder by name for exactly that reason, and this arm is what keeps them from seeing
    /// one.</para>
    ///
    /// <para><b>THE SINK IS UNBOXED AT EACH COMMAND AND THE FORK IS TWO LINES, WHICH IS 6.4's TWO RULES MEETING.
    /// </b> The per-draw classes are consumed through <c>where TSink : struct, IMetalEncoderSink</c> so the JIT
    /// monomorphizes them, and the list may NOT be generic over its sink. So each entry point type-tests for the
    /// shipped sink and hands the generic body either that or a <see cref="MetalRelayEncoderSink"/>. The fork
    /// repeats and the ORDER does not, and what is STRUCTURAL about the fork is that it cannot be SKIPPED: the
    /// boxed <c>_sink</c> field cannot satisfy the struct constraint, so an entry point that handed it straight to
    /// a generic body does not compile. Leaving the RELAY arm off compiles perfectly well, and is caught at
    /// runtime by the device-free rows, which drive a fake sink and so take exactly the arm that would be missing.
    /// A missing step in the order is caught by neither, which is the real asymmetry.</para>
    ///
    /// <para><b>M-A5's END-BEFORE-ANYTHING-ILLEGAL NEEDS NO CODE HERE.</b> A dispatch opens a COMPUTE encoder
    /// through <see cref="MetalEncoderScope.EnsureComputeEncoder"/>, whose first act is to end whatever is open,
    /// so the render pass a dispatch interrupts is closed by the transition itself. Row 12 recorded that as a
    /// decision rather than a gap, and adding a second copy of the invariant here is what it was recorded to
    /// prevent.</para>
    /// </summary>
    internal sealed partial class MetalCommandList
    {
        // THE BOUND INDEX BUFFER, which is RECORDER state and not encoder state: Metal takes the index buffer in
        // the draw call itself, so there is no argument table for a boundary to invalidate. Reset at Begin, in
        // that file's one reset block, and nowhere else. See MetalIndexBinding.
        MetalIndexBinding _indices;

        /// <summary>
        /// THE BOUND INDEX BUFFER AND ITS OFFSET ARITHMETIC. Exposed for the reason <see cref="Passes"/> and
        /// <see cref="GraphicsBinds"/> are: the device-free rows drive the record and read what an indexed draw
        /// would pass, which is the number that draws a different mesh when it is wrong.
        /// </summary>
        internal MetalIndexBinding Indices => _indices;

        /// <inheritdoc/>
        /// <remarks>The no-offset overload, which is the offset overload at zero. A vertex stream's offset is one
        /// number the encoder receives either way, so there is no distinction to preserve.</remarks>
        public void SetVertexBuffer(uint slot, IGpuBuffer b) => SetVertexBuffer(slot, b, 0);

        /// <inheritdoc/>
        /// <remarks>
        /// RESOLVES AND RECORDS. Everything that decides what reaches the encoder is
        /// <see cref="MetalVertexStreamRecords"/>'s: whether the stream is dirty, which contiguous run it joins,
        /// which argument-table index M-B2's numbering puts it at, and the M-R4 invalidation that makes the cache
        /// safe to keep at all.
        /// <para>
        /// A NIL BUFFER IS RECORDED RATHER THAN REFUSED, which is the cache's own rule and differs from a null
        /// RESOURCE SET on purpose: a stream is one argument-table index rather than a whole set's worth, so
        /// writing nil there is how a caller unbinds it. A buffer disposed since it was bound answers nil through
        /// <c>MetalBuffer.Handle</c> and lands on the same arm.
        /// </para>
        /// <para>
        /// THE OFFSET IS THE CALLER'S AND NOTHING COMPOSES IT. A vertex stream is never ring-backed and carries no
        /// set range, which is the whole difference from <see cref="MetalBindRecords"/>, where every buffer bind
        /// is <c>frameBase + rangeOffset + callerDynamicOffset</c>.
        /// </para>
        /// </remarks>
        public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(b);

            MetalBuffer buffer = MetalResourceOwnership.Require<MetalBuffer>(b, _liveness, nameof(b));
            RequireRecording("Binding a vertex buffer");

            _streams.Record(slot, buffer.Handle.Handle, offsetBytes);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// RECORDS THE BUFFER AND ITS ELEMENT WIDTH, and emits nothing, because on Metal there is nothing to
        /// emit: <c>-drawIndexedPrimitives:</c> takes the buffer, the offset and the index type as ARGUMENTS.
        /// <see cref="MetalIndexBinding"/> carries what that means for the invalidation rules, which is that this
        /// is the one record in the backend an encoder boundary leaves alone.
        /// <para>
        /// THERE IS NO USAGE CHECK. Metal has no index-buffer usage bit, so refusing a buffer created without the
        /// seam's <see cref="GpuBufferUsage.IndexBuffer"/> would be a refusal the API does not have, and would
        /// make a call legal on the incumbent and refused on its own replacement.
        /// </para>
        /// </remarks>
        public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(b);

            MetalBuffer buffer = MetalResourceOwnership.Require<MetalBuffer>(b, _liveness, nameof(b));
            RequireRecording("Binding an index buffer");

            _indices.Record(buffer.Handle.Handle, MetalIndexBinding.ToIndexType(fmt));
        }

        /// <inheritdoc/>
        /// <remarks>The single-instance convenience the fullscreen passes call, which is the full overload at
        /// <c>instanceCount = 1</c> with both starts at zero.</remarks>
        public void Draw(uint vertexCount) => Draw(vertexCount, 1, 0, 0);

        /// <inheritdoc/>
        /// <remarks>
        /// <c>-drawPrimitives:vertexStart:vertexCount:instanceCount:baseInstance:</c>, behind the four-step
        /// pre-draw order this type's remarks describe.
        /// <para>
        /// THE TOPOLOGY IS A DRAW ARGUMENT ON THIS API, which is the one place the three backends genuinely
        /// differ: Direct3D 11 sets it on the input assembler and Vulkan bakes it into the pipeline. Row 11
        /// resolves it once at pipeline creation (<c>MetalPipelineState.PrimitiveType</c>), so nothing is mapped
        /// per draw.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">This list is not recording, no framebuffer is bound, or no
        /// graphics pipeline is bound.</exception>
        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
        {
            MetalGraphicsPipeline pipeline = BeginDraw("Drawing", out IntPtr encoder);
            if (encoder == IntPtr.Zero) return;

            if (KhaozEngineMetal.IsPlatformSupported && _sink is MetalEncoderSink native)
            {
                DrawWith(ref native, pipeline, encoder, vertexCount, instanceCount, vertexStart, instanceStart);
                return;
            }

            var relay = new MetalRelayEncoderSink(_sink);
            DrawWith(ref relay, pipeline, encoder, vertexCount, instanceCount, vertexStart, instanceStart);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The indexed arm, identical in every step but the command and one piece of arithmetic:
        /// <paramref name="indexStart"/> is an ELEMENT index on the seam and a BYTE offset in the call, which is
        /// <see cref="MetalIndexBinding.OffsetFor"/>.
        /// <para>
        /// <paramref name="vertexOffset"/> IS SIGNED AND TRAVELS SIGNED, all the way into the selector's
        /// <c>baseVertex</c>: it is added to every index before the vertex buffer is read, and a mesh packed
        /// behind another one in a shared buffer passes a negative one. It is also one of the two arguments that
        /// cross ON THE STACK, which <see cref="ObjCMsgSend.SendVoidDrawIndexedPrimitives"/> carries the whole
        /// argument about.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">This list is not recording, no framebuffer is bound, no
        /// graphics pipeline is bound, or no live index buffer is bound.</exception>
        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset,
            uint instanceStart)
        {
            MetalGraphicsPipeline pipeline = RequireGraphicsPipeline("Drawing indexed");

            // THE INDEX REFUSAL COMES BEFORE THE PASS OPENS, beside the pipeline refusal and for its reason: a
            // refusal spends nothing. PrepareDraw opens the encoder and CONSUMES the pending clears, so refusing
            // after it would cost a boundary and swallow the clears of a frame that never drew.
            //
            // It also lands before M-W5's nil arm, which the ordering above gives for free and which is the half
            // worth naming: a recording that forgot SetIndexBuffer has made a caller error whether or not this
            // frame's drawable arrived, so the orphan target must not SWALLOW it. A frame that silently dropped
            // the mistake would report it on the next frame that happened to get a drawable, which is a bug
            // report about the wrong frame.
            if (_indices.DrawRefusal() is { } refusal) throw new InvalidOperationException(refusal);

            IntPtr encoder = _passes.PrepareDraw();
            if (encoder == IntPtr.Zero) return;

            if (KhaozEngineMetal.IsPlatformSupported && _sink is MetalEncoderSink native)
            {
                DrawIndexedWith(ref native, pipeline, encoder, indexCount, instanceCount, indexStart,
                    vertexOffset, instanceStart);
                return;
            }

            var relay = new MetalRelayEncoderSink(_sink);
            DrawIndexedWith(ref relay, pipeline, encoder, indexCount, instanceCount, indexStart, vertexOffset,
                instanceStart);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// <c>-dispatchThreadgroups:threadsPerThreadgroup:</c>, with the group size read off the SHADER rather
        /// than off a description nothing validates: Metal is the one backend that needs the number at the
        /// dispatch, where the other two read it out of the compiled module, so row 9's <c>SpirvLocalSize</c>
        /// travels here through <c>MetalComputePipeline.Shader</c>.
        /// <para>
        /// THE ENCODER IS SERIAL (M-H4), AND THAT IS WHAT MAKES RULE 2 HOLD WITH NO HAZARD MACHINERY. Two
        /// dispatches inside one compute encoder do not overlap, so a dependent chain inside ONE recording is
        /// ordered by the encoder itself and this backend needs no barrier batch, no layout tracker and no
        /// read-after-write analysis. That is a BACKEND PROPERTY and not a contract change: the seam's compute
        /// rule 2 still says a dependent chain needs <c>End</c>, <c>Submit</c> and <c>WaitForIdle</c>, because
        /// the other legs need the drain and a consumer that dropped it because this backend tolerates the chain
        /// would break on them.
        /// </para>
        /// <para>
        /// OPENING THE COMPUTE ENCODER IS WHAT ENDS AN OPEN RENDER PASS (M-A5), through the scope rather than
        /// through anything written here.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">This list is not recording, or no compute pipeline is
        /// bound.</exception>
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireRecording("Dispatching compute work");

            MetalComputePipeline pipeline = _pipelines.Compute ?? throw new InvalidOperationException(
                "A dispatch was recorded on a native Metal command list with no compute pipeline bound. Call "
                + "SetComputePipeline first. Metal takes the threadgroup size as an ARGUMENT to "
                + "dispatchThreadgroups:threadsPerThreadgroup: rather than reading it out of the bound kernel the "
                + "way Direct3D 11 and Vulkan do, so without a pipeline this backend does not know how many "
                + "threads a group has, quite apart from having no kernel to run.");

            IntPtr encoder = _encoders.EnsureComputeEncoder();

            // A NIL COMPUTE ENCODER IS A COMMAND BUFFER IN A STATE METAL WILL NOT ENCODE INTO, which is a device
            // already in trouble rather than M-W5's orphan target (that one is about a render encoder and a nil
            // drawable). It takes the same arm regardless, because the alternative is a bind flush writing into
            // nil and marking its records clean.
            if (encoder == IntPtr.Zero) return;

            if (KhaozEngineMetal.IsPlatformSupported && _sink is MetalEncoderSink native)
            {
                DispatchWith(ref native, pipeline, encoder, groupCountX, groupCountY, groupCountZ);
                return;
            }

            var relay = new MetalRelayEncoderSink(_sink);
            DispatchWith(ref relay, pipeline, encoder, groupCountX, groupCountY, groupCountZ);
        }

        // ---- The generic bodies, where the ORDER lives -------------------------------------------------------

        void DrawWith<TSink>(ref TSink sink, MetalGraphicsPipeline pipeline, IntPtr encoder, uint vertexCount,
            uint instanceCount, uint vertexStart, uint instanceStart)
            where TSink : struct, IMetalEncoderSink
        {
            PrepareGraphics(ref sink, pipeline, encoder);
            sink.Draw(encoder, pipeline.State.PrimitiveType, vertexStart, vertexCount, instanceCount,
                instanceStart);
        }

        void DrawIndexedWith<TSink>(ref TSink sink, MetalGraphicsPipeline pipeline, IntPtr encoder,
            uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
            where TSink : struct, IMetalEncoderSink
        {
            PrepareGraphics(ref sink, pipeline, encoder);
            sink.DrawIndexed(encoder, pipeline.State.PrimitiveType, indexCount, _indices.Buffer,
                _indices.OffsetFor(indexStart), _indices.IndexType == MTLIndexType.UInt16, instanceCount,
                vertexOffset, instanceStart);
        }

        void DispatchWith<TSink>(ref TSink sink, MetalComputePipeline pipeline, IntPtr encoder, uint groupCountX,
            uint groupCountY, uint groupCountZ)
            where TSink : struct, IMetalEncoderSink
        {
            if (_pipelines.NeedsComputeStateBlock(_encoders.Epoch))
            {
                _compute.SetComputePipelineState(encoder, pipeline.State.Handle);
                _pipelines.MarkComputeStateBlockEmitted(_encoders.Epoch);
            }

            FlushComputeBinds(ref sink, encoder);

            MetalComputeShader shader = pipeline.Shader;
            sink.Dispatch(encoder, groupCountX, groupCountY, groupCountZ, shader.ThreadGroupSizeX,
                shader.ThreadGroupSizeY, shader.ThreadGroupSizeZ);
        }

        // THE THREE STEPS BETWEEN THE OPEN ENCODER AND THE COMMAND, in ONE place so a draw cannot be written that
        // skips one.
        void PrepareGraphics<TSink>(ref TSink sink, MetalGraphicsPipeline pipeline, IntPtr encoder)
            where TSink : struct, IMetalEncoderSink
        {
            if (_pipelines.NeedsGraphicsStateBlock(_encoders.Epoch))
            {
                MetalGraphicsStateBlock block = MetalGraphicsStateBlock.For(
                    pipeline, _passes.BoundFramebuffer.HasDepth);

                _render.SetGraphicsState(encoder, in block);
                _pipelines.MarkGraphicsStateBlockEmitted(_encoders.Epoch);
            }

            FlushGraphicsBinds(ref sink, encoder);
            FlushVertexStreams(ref sink, encoder);
        }

        // THE FIRST STEP, plus the two things every graphics command needs true. Every refusal is spent BEFORE
        // the pass opens, which is why the two halves are separate members: DrawIndexed has a third refusal of its
        // own and it belongs between them.
        MetalGraphicsPipeline BeginDraw(string what, out IntPtr encoder)
        {
            MetalGraphicsPipeline pipeline = RequireGraphicsPipeline(what);

            encoder = _passes.PrepareDraw();
            return pipeline;
        }

        // EVERYTHING A GRAPHICS COMMAND REFUSES ON, and nothing that spends anything. PrepareDraw opens an encoder
        // and consumes the pending clears, so a guard that ran after it would cost a boundary and lose the clears
        // for a command that never happened.
        MetalGraphicsPipeline RequireGraphicsPipeline(string what)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireRecording(what);

            return _pipelines.Graphics ?? throw new InvalidOperationException(
                what + " was recorded on a native Metal command list with no graphics pipeline bound. Call "
                + "SetPipeline first. A pipeline is where the render pipeline state, the rasterizer state and the "
                + "TOPOLOGY all come from, and on this API the topology is an argument to the draw call itself, "
                + "so there is nothing to fall back to.");
        }
    }
}
