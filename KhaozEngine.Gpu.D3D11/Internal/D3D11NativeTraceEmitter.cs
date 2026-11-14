using System;
using System.Globalization;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE REAL EMITTER'S GUARDS WITH NO DEVICE UNDER THEM: every rule this row owns (the one
    /// <c>ClearState</c> per replay of R3, the redundancy caches of R6, the precise scrub of R8, and the
    /// framebuffer-change-guarded viewport and scissor of W6) applied through the device-owned
    /// <see cref="D3D11DeviceState"/>, with the resulting <c>ID3D11DeviceContext</c> calls written to a
    /// <see cref="D3D11NativeCallLog"/> instead of made.
    /// <para>
    /// WHY THIS IS NOT A SECOND IMPLEMENTATION OF THE GUARDS. Every decision above is taken inside
    /// <see cref="D3D11DeviceState"/>, which the real emitter will use unchanged. What lives here is only the
    /// translation from "this changed" into "this call", so the thing a test pins is the shipped guard and not a
    /// copy of it. That is the difference between this and a harness that reproduces the logic, which would drift
    /// the first time a guard was tightened.
    /// </para>
    /// <para>
    /// THE REFUSALS ARE THE SHIPPED ONES TOO. Every handle the real emitter resolves (a framebuffer, an index,
    /// vertex, written or copied buffer, a bound resource, a compute pipeline) is resolved here through the same
    /// <see cref="D3D11BindResolve"/> member in the same order relative to the redundancy guards, so a foreign
    /// handle is refused with the same exception, reason and fields. A test therefore drives this type with the
    /// backend's own handle types or with fakes that answer the same device-free seams
    /// (<see cref="ID3D11RenderTargetSurface"/>, <see cref="ID3D11BindableViews"/>,
    /// <see cref="ID3D11ComputePipelineState"/>). The graphics pipeline and the texture transfers are the two
    /// splits left, and each says why where it lives.
    /// </para>
    /// <para>
    /// THE BIND FLUSH IS MODELLED TOO, and that is the sink decision of work-breakdown row 9. The schedule of
    /// decision R5 and the array-batched fan-out of decision R6 live in <see cref="D3D11BindFlush"/> and
    /// <see cref="D3D11SetActivation"/>, which the real emitter uses unchanged, so this type supplies only the
    /// <see cref="ID3D11BindSink"/> end of them: which method name a register file plus a stage picks. A budget
    /// taken here therefore measures the shipped dirty tracking, the shipped slot order, the shipped drain and the
    /// shipped register arithmetic, and can drift from the real replay path in <see cref="D3D11NativeCallName"/>
    /// alone. A resource-set BIND still lands as <see cref="D3D11NativeCall.ResourceSetPending"/>, which holds its
    /// place in the order and is excluded from the total, because a bind genuinely issues nothing.
    /// </para>
    /// <para>
    /// A readonly struct over two class references, which is the shape <see cref="ID3D11Emitter"/> requires and a
    /// reflection test enforces. It RECEIVES its state rather than constructing one, which is the discipline
    /// issue #476 is about: an emitter that allocated its own would satisfy the readonly rule and still give
    /// every command list its own cache. The bind flush rides that same state, so it is one per device by
    /// construction rather than by a second rule.
    /// </para>
    /// </summary>
    internal readonly partial struct D3D11NativeTraceEmitter : ID3D11Emitter, ID3D11BindSink
    {
        readonly D3D11DeviceState _state;
        readonly D3D11NativeCallLog _log;

        internal D3D11NativeTraceEmitter(D3D11DeviceState state, D3D11NativeCallLog log)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>The device-owned state this emitter reads and updates. Every emitter value the device hands
        /// out addresses the same one.</summary>
        internal D3D11DeviceState State => _state;

        /// <summary>Where the counts and the trace land.</summary>
        internal D3D11NativeCallLog Log => _log;

        /// <summary>
        /// DECISION R3's SINGLE <c>ClearState</c>, and the reason the caches can be trusted at all. Under the
        /// deferred driver the replay raises this around the stored ops, so it happens once per submit, and the
        /// cache is reset immediately after so cache and context agree at the one moment they are guaranteed to.
        /// </summary>
        public void Begin()
        {
            _log.Record(D3D11NativeCall.ClearState);
            _state.Reset();
        }

        /// <summary>Closing the scope issues nothing. The monotonic completion-fence signal at the end of a
        /// replay is the fence primitive of work-breakdown row 13a and is not built here.</summary>
        public void End()
        {
        }

        /// <summary>
        /// DECISION W6, the whole of it. A framebuffer CHANGE is three calls, an <c>OMSetRenderTargets</c> plus
        /// the full viewport and the full scissor that Veldrid's base <c>SetFramebuffer</c> auto-applies, and a
        /// redundant re-bind is zero calls and leaves the viewport and the scissor exactly as they were. There is
        /// no <c>SetViewport</c> on the seam, so this is the only place a viewport is ever set.
        /// <para>
        /// The surface is resolved through the shared seam BEFORE the identity guard, as the real emitter does,
        /// so a framebuffer from another backend is refused here too, on a redundant re-bind as well as on a
        /// change (https://github.com/APKiwiOrg/KhaozEngine/issues/1691).
        /// </para>
        /// </summary>
        public void SetFramebuffer(IGpuFramebuffer framebuffer)
        {
            D3D11BindResolve.RenderTargets(framebuffer);
            if (!_state.BindFramebuffer(framebuffer)) return;

            _log.Record(D3D11NativeCall.OMSetRenderTargets, _log.Id(framebuffer));
            _log.Record(D3D11NativeCall.RSSetViewports, FullViewport(framebuffer));
            _log.Record(D3D11NativeCall.RSSetScissorRects,
                D3D11ScissorState.Describe(_state.Scissors.SetFull(framebuffer.Width, framebuffer.Height)));
        }

        /// <summary>
        /// A clear names an attachment of the BOUND framebuffer, so both refusals are asked of the shared seam
        /// before anything is recorded: with none bound, or with no attachment at that index, the real emitter
        /// throws and a trace that recorded the call anyway would model a frame the device refuses.
        /// </summary>
        public void ClearColorTarget(uint index, Color rgba)
        {
            D3D11BindResolve.RequireColourAttachment(_state.BoundFramebuffer, index);
            _log.Record(D3D11NativeCall.ClearRenderTargetView,
                $"{N(index)},{N(rgba.R)},{N(rgba.G)},{N(rgba.B)},{N(rgba.A)}");
        }

        /// <inheritdoc cref="ClearColorTarget"/>
        public void ClearDepthStencil(float depth)
        {
            D3D11BindResolve.RequireDepthAttachment(_state.BoundFramebuffer);
            _log.Record(D3D11NativeCall.ClearDepthStencilView, N(depth));
        }

        /// <summary>
        /// DECISION R6: one native call per pipeline-level object that ACTUALLY changed, and nothing at all for a
        /// rebind of the pipeline already bound. The calls are issued in cache-slot order, which D3D11 does not
        /// require and a readable trace does.
        /// <para>
        /// THE PENDING SETS ARE DRAINED FIRST (decision R5, rule 5), under the OUTGOING pipeline's layouts,
        /// because those layouts are what numbers the registers a mark recorded under the outgoing pipeline
        /// belongs at. So the drained binds appear in the trace AHEAD of this pipeline's state calls, which is
        /// where they happened.
        /// </para>
        /// <para>
        /// THE OWNERSHIP REFUSAL IS THIS TYPE'S OWN, not the shared seam's. Both emitters refuse a pipeline from
        /// another backend with <see cref="GpuResourceOwnershipRefusal.ForeignBackend"/>, but the real emitter
        /// names its concrete <see cref="D3D11GraphicsPipeline"/>, which only a device can build, so this one
        /// asks for the device-free seam that pipeline answers and its <c>ExpectedType</c> says so.
        /// </para>
        /// </summary>
        public void SetPipeline(IGpuPipeline pipeline)
        {
            if (pipeline is null) throw new ArgumentNullException(nameof(pipeline));
            if (pipeline is not ID3D11PipelineState state)
                throw new GpuResourceOwnershipArgumentException(GpuResourceOwnershipRefusal.ForeignBackend,
                    $"A {pipeline.GetType().Name} reached the native Direct3D 11 emitter as a graphics pipeline. "
                    + "A pipeline this backend created also implements ID3D11PipelineState, which is what the "
                    + "redundancy caches of decision R6 compare against, so a pipeline without it would rebind "
                    + "all seven state objects on every draw.", nameof(pipeline))
                {
                    ExpectedBackend = GpuBackendKind.Direct3D11Native,
                    ActualType = pipeline.GetType(),
                    ExpectedType = typeof(ID3D11PipelineState),
                    ResourceKind = "pipeline",
                    Context = "graphics pipeline",
                    PipelineKind = "graphics",
                };

            D3D11NativeTraceEmitter sink = this;
            _state.Binds.SetGraphicsPipeline(ref sink, pipeline);

            D3D11StateChange changed = _state.BindPipeline(state);
            if (changed == D3D11StateChange.None) return;

            if (Has(changed, D3D11StateChange.VertexShader))
                _log.Record(D3D11NativeCall.VSSetShader, _log.Id(state.VertexShader));
            if (Has(changed, D3D11StateChange.PixelShader))
                _log.Record(D3D11NativeCall.PSSetShader, _log.Id(state.PixelShader));
            // The two calls that carry an argument riding the pipeline (issue #454). The factor and the reference
            // are in the TRACE as well as in the key, because a trace that showed only the state object could not
            // tell the two pipelines of the hazard apart and a test over it would pass either way.
            if (Has(changed, D3D11StateChange.BlendState))
                _log.Record(D3D11NativeCall.OMSetBlendState,
                    $"{_log.Id(state.BlendState)},{Factor(state.BlendFactor)}");
            if (Has(changed, D3D11StateChange.DepthStencilState))
                _log.Record(D3D11NativeCall.OMSetDepthStencilState,
                    $"{_log.Id(state.DepthStencilState)},{N(state.StencilReference)}");
            if (Has(changed, D3D11StateChange.RasterizerState))
                _log.Record(D3D11NativeCall.RSSetState, _log.Id(state.RasterizerState));
            if (Has(changed, D3D11StateChange.InputLayout))
                _log.Record(D3D11NativeCall.IASetInputLayout, _log.Id(state.InputLayout));
            if (Has(changed, D3D11StateChange.PrimitiveTopology))
                _log.Record(D3D11NativeCall.IASetPrimitiveTopology, N(state.PrimitiveTopology));
        }

        /// <summary>
        /// RECORDED, NOT EMITTED (decision R5, rule 1). The slot is compared against what it already holds and
        /// left owing a full activation, an offsets-only push or nothing, and the next draw pays it. The trace
        /// line is <see cref="D3D11NativeCall.ResourceSetPending"/>, which holds the bind's place in the order and
        /// counts as no native call at all.
        /// </summary>
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
        {
            _log.Record(D3D11NativeCall.ResourceSetPending, $"gfx,{N(slot)},{_log.Id(set)}");
            _state.Binds.RecordGraphics(slot, set, 0u, hasDynamicOffset: false);
        }

        /// <inheritdoc cref="SetGraphicsResourceSet(uint, IGpuResourceSet)"/>
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
        {
            _log.Record(D3D11NativeCall.ResourceSetPending, $"gfx,{N(slot)},{_log.Id(set)},{N(dynamicOffset)}");
            _state.Binds.RecordGraphics(slot, set, dynamicOffset, hasDynamicOffset: true);
        }

        /// <summary>
        /// RECORDED, NOT EMITTED, which is what makes 5.3's <c>IASetVertexBuffers(0, 2, ...)</c> reachable: two
        /// per-stream calls cannot be collapsed after they have been made, and the stride the call needs comes
        /// from the pipeline rather than from this bind. The next draw issues the batch. The trace line is
        /// <see cref="D3D11NativeCall.VertexBufferPending"/>, which holds the bind's place and counts as no
        /// native call.
        /// </summary>
        public void SetVertexBuffer(uint slot, IGpuBuffer buffer, uint offsetBytes)
        {
            _log.Record(D3D11NativeCall.VertexBufferPending, $"{N(slot)},{_log.Id(buffer)},{N(offsetBytes)}");
            _state.Vertices.RecordVertexBuffer(slot, buffer, offsetBytes);
        }

        /// <summary>Issued at the bind, guarded by the redundancy cache over the pair (buffer, format). There is
        /// nothing to batch an index bind with, since <c>IASetIndexBuffer</c> binds exactly one. The buffer is
        /// resolved through the shared seam BEFORE that cache, as the real emitter does, so a buffer from another
        /// backend is refused on every bind rather than recorded once and then passed as redundant.</summary>
        public void SetIndexBuffer(IGpuBuffer buffer, GpuIndexFormat format)
        {
            D3D11BindResolve.NativeBuffer(buffer);
            if (!_state.Vertices.BindIndexBuffer(buffer, format)) return;

            _log.Record(D3D11NativeCall.IASetIndexBuffer, $"{_log.Id(buffer)},{format}");
        }

        /// <summary>
        /// An explicit scissor overrides whatever a framebuffer bind left behind, and nothing undoes it: the only
        /// things that touch the scissor afterwards are another explicit call, a genuine framebuffer CHANGE, and
        /// the <c>ClearState</c> that opens the next replay.
        /// <para>
        /// Traced as <c>count:&lt;count&gt;,[left,top,right,bottom],...</c>. The rectangles are the whole emitted
        /// prefix from slot zero through the highest retained slot, in D3D11 <c>RECT</c> form rather than the
        /// seam's origin-plus-size, so a trace can be read straight against a capture.
        /// </para>
        /// </summary>
        public void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
        {
            _log.Record(D3D11NativeCall.RSSetScissorRects,
                D3D11ScissorState.Describe(_state.Scissors.Set(index, x, y, width, height)));
        }

        public void SetFullScissorRects()
        {
            // Refused in the one place both emitters ask, so the two cannot drift in wording or in condition.
            IGpuFramebuffer framebuffer = D3D11BindResolve.RequireScissorExtent(_state.BoundFramebuffer);

            _log.Record(D3D11NativeCall.RSSetScissorRects,
                D3D11ScissorState.Describe(_state.Scissors.SetFull(framebuffer.Width, framebuffer.Height)));
        }

        /// <summary>Decision R5, rule 2, in the order every draw path in this backend takes: the resource-set
        /// flush FIRST, then the batched vertex streams, then the draw. The topology and the blend factor were
        /// paid at the pipeline bind, which is where they ride (5.3).</summary>
        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
        {
            FlushGraphicsBinds();
            FlushVertexBuffers();
            _log.Record(D3D11NativeCall.DrawInstanced,
                $"{N(vertexCount)},{N(instanceCount)},{N(vertexStart)},{N(instanceStart)}");
        }

        /// <inheritdoc cref="Draw"/>
        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset,
            uint instanceStart)
        {
            FlushGraphicsBinds();
            FlushVertexBuffers();
            _log.Record(D3D11NativeCall.DrawIndexedInstanced,
                $"{N(indexCount)},{N(instanceCount)},{N(indexStart)},{N(vertexOffset)},{N(instanceStart)}");
        }

        /// <summary>The buffer is resolved through the shared seam exactly when the real emitter names it, which
        /// is every write it issues: a ring-backed buffer writes into its mapped segment and an empty write is a
        /// no-op, and neither asks.</summary>
        public void UpdateBuffer(IGpuBuffer buffer, uint offsetBytes, ReadOnlySpan<byte> data)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            if (buffer is not ID3D11RingBacked { Ring: not null } && !data.IsEmpty) D3D11BindResolve.NativeBuffer(buffer);

            _log.Record(D3D11NativeCall.UpdateSubresource, $"{_log.Id(buffer)},{N(offsetBytes)},{N(data.Length)}b");
        }

        /// <summary>Both buffers are resolved through the shared seam, destination first, whenever the real
        /// emitter issues the copy, which is every copy of a non-zero size.</summary>
        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes,
            uint sizeInBytes)
        {
            if (sizeInBytes != 0)
            {
                D3D11BindResolve.NativeBuffer(dst);
                D3D11BindResolve.NativeBuffer(src);
            }

            _log.Record(D3D11NativeCall.CopySubresourceRegion,
                $"{_log.Id(src)},{N(srcOffsetBytes)},{_log.Id(dst)},{N(dstOffsetBytes)},{N(sizeInBytes)}");
        }

        /// <summary>
        /// THE TEXTURE TRANSFERS APPLY NO OWNERSHIP REFUSAL HERE, and that is the one split this type keeps. The
        /// real emitter refuses anything that is not its concrete <see cref="D3D11Texture"/>, which only a device
        /// can build, so a trace that asked the same question could never be handed a texture it accepts.
        /// </summary>
        public void CopyTexture(IGpuTexture src, IGpuTexture dst)
            => _log.Record(D3D11NativeCall.CopyResource, $"{_log.Id(src)},{_log.Id(dst)}");

        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
            IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
            => _log.Record(D3D11NativeCall.CopySubresourceRegion,
                $"{_log.Id(src)},{N(srcMipLevel)},{N(srcArrayLayer)},{_log.Id(dst)},{N(dstMipLevel)},"
                + $"{N(dstArrayLayer)},{N(width)},{N(height)}");

        public void GenerateMipmaps(IGpuTexture texture)
            => _log.Record(D3D11NativeCall.GenerateMips, _log.Id(texture));

        public void ResolveTexture(IGpuTexture src, IGpuTexture dst)
            => _log.Record(D3D11NativeCall.ResolveSubresource, $"{_log.Id(src)},{_log.Id(dst)}");

        /// <summary>
        /// The shader itself is bound unguarded, deliberately: a compute pipeline is bound a handful of times a
        /// frame where a graphics one is bound hundreds, so a redundancy slot for it would cost a compare per
        /// dispatch to save a call nothing measures. <see cref="D3D11ComputePipeline"/> carries the reasoning.
        /// <para>
        /// The pipeline-switch DRAIN happens here, on the compute dirty array, for the same reason as the graphics
        /// one: a compute set's registers are numbered under its pipeline's layout array.
        /// </para>
        /// </summary>
        public void SetComputePipeline(IGpuComputePipeline pipeline)
        {
            D3D11BindResolve.ComputePipelineState(pipeline);

            D3D11NativeTraceEmitter sink = this;
            _state.Binds.SetComputePipeline(ref sink, pipeline);
            _log.Record(D3D11NativeCall.CSSetShader, _log.Id(pipeline));
        }

        /// <inheritdoc cref="SetGraphicsResourceSet(uint, IGpuResourceSet)"/>
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set)
        {
            _log.Record(D3D11NativeCall.ResourceSetPending, $"cs,{N(slot)},{_log.Id(set)}");
            _state.Binds.RecordCompute(slot, set, 0u, hasDynamicOffset: false);
        }

        /// <inheritdoc cref="SetGraphicsResourceSet(uint, IGpuResourceSet)"/>
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
        {
            _log.Record(D3D11NativeCall.ResourceSetPending, $"cs,{N(slot)},{_log.Id(set)},{N(dynamicOffset)}");
            _state.Binds.RecordCompute(slot, set, dynamicOffset, hasDynamicOffset: true);
        }

        /// <summary>Decision R5, rule 2, on the compute side: the pre-command hook first, then the dispatch.
        /// Decision C1's SRV-versus-UAV auto-unbind rides inside that hook, where the bind arrays are assembled
        /// (<see cref="D3D11ViewConflicts"/>), which is what makes the whole of C1 visible in this trace.</summary>
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            FlushComputeBinds();
            _log.Record(D3D11NativeCall.Dispatch,
                $"{N(groupCountX)},{N(groupCountY)},{N(groupCountZ)}");
        }

        /// <summary>
        /// THE PRE-COMMAND HOOK, and the shape work-breakdown row 10 consumes. An emitter's draw path calls it
        /// FIRST, before the vertex and index binds and before the draw, and then issues. Kept as a named method
        /// rather than inlined at each draw so the position is one thing to get right and the real emitter's draw
        /// path reads the same as this one's.
        /// </summary>
        internal void FlushGraphicsBinds()
        {
            D3D11NativeTraceEmitter sink = this;
            _state.Binds.FlushGraphics(ref sink);
        }

        /// <inheritdoc cref="FlushGraphicsBinds"/>
        internal void FlushComputeBinds()
        {
            D3D11NativeTraceEmitter sink = this;
            _state.Binds.FlushCompute(ref sink);
        }

        /// <summary>
        /// THE BATCHED VERTEX FLUSH: one <c>IASetVertexBuffers</c> over the contiguous span of dirty slots, or
        /// nothing when no stream changed. The span may sweep in a clean slot between two dirty ones, which
        /// rebinds it to what it already holds and keeps the law at one call.
        /// </summary>
        internal void FlushVertexBuffers()
        {
            if (!_state.Vertices.TakeFlush(out uint startSlot, out int count)) return;

            RecordVertexBuffers(startSlot, count);
        }

        // ONE IASetVertexBuffers OVER A SPAN, CARRYING WHAT THE RECORD HOLDS, which is the rule rather than the
        // flush's private arrangement: the scrub writes its span through here too, so a live slot straddled by two
        // scrubbed ones is rebound to what it already holds instead of being unbound behind the record's back.
        // Every live buffer in the span is resolved through the shared seam before the call, as the real emitter
        // resolves each one it passes, so a vertex buffer from another backend is refused at the draw.
        void RecordVertexBuffers(uint startSlot, int count)
        {
            var text = new System.Text.StringBuilder();
            text.Append(N(startSlot)).Append(',').Append(N(count));
            for (uint slot = startSlot; slot < startSlot + (uint)count; slot++)
            {
                IGpuBuffer? buffer = _state.Vertices.BufferAt(slot);
                if (buffer is not null) D3D11BindResolve.NativeBuffer(buffer);

                text.Append(',').Append(_log.Id(buffer))
                    .Append('@').Append(N(_state.Vertices.OffsetAt(slot)))
                    .Append('/').Append(N(_state.Vertices.StrideAt(slot)));
            }

            _log.Record(D3D11NativeCall.IASetVertexBuffers, text.ToString());
        }

        // ---- ID3D11BindSink: the naming translation, and the only thing a device-free budget can drift in ----
        //
        // Each bind resolves every entry through the shared seam in the order the real emitter does, so a resource
        // from another backend, a resource with no view for its register file and a window against no buffer are
        // refused here as they are on the device. Only the unordered-access bind names its call first, because
        // the real emitter does: that name is itself the refusal for every non-compute stage.

        /// <inheritdoc/>
        public void SetConstantBuffers(GpuShaderStages stage, uint startSlot,
            ReadOnlySpan<D3D11ConstantBufferBind> binds)
        {
            if (binds.Length != 0)
            {
                D3D11BindResolve.Constants(binds, new int[binds.Length], new int[binds.Length]);
                for (int i = 0; i < binds.Length; i++)
                    D3D11BindResolve.ViewOf(binds[i].Buffer, D3D11RegisterFile.ConstantBuffer);
            }

            _log.Record(D3D11NativeCallName.ConstantBuffers(stage),
                $"{N(startSlot)},{N(binds.Length)},{DescribeConstants(binds)}");
        }

        /// <inheritdoc/>
        public void UnsetConstantBuffers(GpuShaderStages stage, uint startSlot, int count)
            => _log.Record(D3D11NativeCallName.ConstantBuffers(stage), $"{N(startSlot)},{N(count)},unset");

        /// <inheritdoc/>
        public void SetShaderResources(GpuShaderStages stage, uint startSlot,
            ReadOnlySpan<IGpuBindableResource?> resources)
        {
            ResolveViews(resources, D3D11RegisterFile.ShaderResource);
            _log.Record(D3D11NativeCallName.ShaderResources(stage),
                $"{N(startSlot)},{N(resources.Length)},{Describe(resources)}");
        }

        /// <inheritdoc/>
        public void SetSamplers(GpuShaderStages stage, uint startSlot, ReadOnlySpan<IGpuBindableResource?> samplers)
        {
            ResolveViews(samplers, D3D11RegisterFile.Sampler);
            _log.Record(D3D11NativeCallName.Samplers(stage),
                $"{N(startSlot)},{N(samplers.Length)},{Describe(samplers)}");
        }

        /// <inheritdoc/>
        public void SetUnorderedAccessViews(GpuShaderStages stage, uint startSlot,
            ReadOnlySpan<IGpuBindableResource?> views)
        {
            D3D11NativeCall call = D3D11NativeCallName.UnorderedAccessViews(stage);
            ResolveViews(views, D3D11RegisterFile.UnorderedAccess);
            _log.Record(call, $"{N(startSlot)},{N(views.Length)},{Describe(views)}");
        }

        static void ResolveViews(ReadOnlySpan<IGpuBindableResource?> resources, D3D11RegisterFile file)
        {
            for (int i = 0; i < resources.Length; i++) D3D11BindResolve.ViewOf(resources[i], file);
        }

        // One entry per register in the span, separated by '|', so a reader can see which register got what and a
        // hole is visibly a hole rather than an absent entry that shifts everything after it. A constant-buffer
        // entry carries the window the way *SetConstantBuffers1 takes it: id@firstConstant+constantCount.
        string DescribeConstants(ReadOnlySpan<D3D11ConstantBufferBind> binds)
        {
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < binds.Length; i++)
            {
                if (i > 0) text.Append('|');
                if (binds[i].Buffer is null)
                {
                    text.Append('-');
                    continue;
                }

                text.Append(_log.Id(binds[i].Buffer)).Append('@').Append(N(binds[i].FirstConstant))
                    .Append('+').Append(N(binds[i].ConstantCount));
            }

            return text.ToString();
        }

        string Describe(ReadOnlySpan<IGpuBindableResource?> resources)
        {
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < resources.Length; i++)
            {
                if (i > 0) text.Append('|');
                text.Append(resources[i] is null ? "-" : _log.Id(resources[i]));
            }

            return text.ToString();
        }

        /// <summary>
        /// DECISION R8, and NOT a seam member. A resource being disposed is the one moment a cache can be left
        /// naming an object that no longer exists, so the device calls this from its resource disposal: the state
        /// forgets the resource wherever it was cached, and exactly those slots are unbound. A wholesale
        /// <c>ClearState</c> would also be correct and is rejected, since it makes the next draw rebind
        /// everything to drop one object.
        /// <para>
        /// A resource that was never bound, or was replaced before it was disposed, scrubs to nothing and issues
        /// nothing, which is the common case.
        /// </para>
        /// </summary>
        internal void ScrubDisposed(object resource)
        {
            D3D11StateChange scrubbed = _state.Scrub(resource, out uint vertexStart, out int vertexCount);
            if (scrubbed == D3D11StateChange.None) return;

            if (Has(scrubbed, D3D11StateChange.VertexShader))
                _log.Record(D3D11NativeCall.VSSetShader, "null");
            if (Has(scrubbed, D3D11StateChange.PixelShader))
                _log.Record(D3D11NativeCall.PSSetShader, "null");
            if (Has(scrubbed, D3D11StateChange.BlendState))
                _log.Record(D3D11NativeCall.OMSetBlendState, $"null,{Factor(D3D11DeviceState.ClearedBlendFactor)}");
            if (Has(scrubbed, D3D11StateChange.DepthStencilState))
                _log.Record(D3D11NativeCall.OMSetDepthStencilState, "null,0");
            if (Has(scrubbed, D3D11StateChange.RasterizerState))
                _log.Record(D3D11NativeCall.RSSetState, "null");
            if (Has(scrubbed, D3D11StateChange.InputLayout))
                _log.Record(D3D11NativeCall.IASetInputLayout, "null");
            if (Has(scrubbed, D3D11StateChange.VertexBuffers)) RecordVertexBuffers(vertexStart, vertexCount);
            if (Has(scrubbed, D3D11StateChange.IndexBuffer))
                _log.Record(D3D11NativeCall.IASetIndexBuffer, "null");
            if (Has(scrubbed, D3D11StateChange.Framebuffer))
                _log.Record(D3D11NativeCall.OMSetRenderTargets, "null");
        }

        static bool Has(D3D11StateChange changed, D3D11StateChange flag) => (changed & flag) != 0;

        // The full viewport a framebuffer change brings: the whole target, depth 0 to 1, exactly what Veldrid's
        // SetFullViewports produces for a single output.
        static string FullViewport(IGpuFramebuffer framebuffer)
            => $"1,0,0,{N(framebuffer.Width)},{N(framebuffer.Height)},0,1";

        // A blend factor as the four components OMSetBlendState takes, so two pipelines that share a state object
        // and differ only here are visibly different in the trace.
        static string Factor(System.Numerics.Vector4 factor)
            => $"{N(factor.X)}|{N(factor.Y)}|{N(factor.Z)}|{N(factor.W)}";

        // Invariant culture throughout, so a trace compares equal on a machine whose decimal separator is a
        // comma, matching the emitter call log.
        static string N(uint value) => value.ToString(CultureInfo.InvariantCulture);
        static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
        static string N(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    }
}
