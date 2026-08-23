using System;
using System.Runtime.Versioning;
using Vortice.Direct3D11;
using Vortice;
using Vortice.Mathematics;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE REAL EMITTER: every <see cref="ID3D11Emitter"/> call turned into the <c>ID3D11DeviceContext</c> calls
    /// it names, through the SAME device-owned guards the device-free
    /// <see cref="D3D11NativeTraceEmitter"/> applies. This is the type a frame actually renders through, and it
    /// is deliberately the thinner of the two: every decision (the one <c>ClearState</c> of R3, the redundancy
    /// caches of R6, the batching of the vertex streams, the schedule of R5, the precise scrub of R8, the
    /// framebuffer-guarded viewport of W6) is taken inside <see cref="D3D11DeviceState"/>,
    /// <see cref="D3D11BindFlush"/>, <see cref="D3D11VertexStreams"/> and <see cref="D3D11SetActivation"/>, which
    /// this type uses unchanged, and every REFUSAL a stream can earn (the scissor index, a buffer or a framebuffer
    /// from another backend, a clear against no target or against an attachment the bound framebuffer does not
    /// have) is taken inside <see cref="D3D11BindResolve"/>, which the trace emitter asks in the same order. What
    /// is left here is the translation into a Vortice call, plus two casts that cannot be pushed down: the
    /// <c>object</c> a resolve answers with into its Direct3D type, and a backstop for a framebuffer that declares
    /// a depth attachment while carrying no view for it, which is a defect in this backend's own construction
    /// rather than anything a stream can express.
    /// <para>
    /// THAT SPLIT IS THE WHOLE TEST STRATEGY, and it is worth being blunt about what it does and does not buy.
    /// There is no Direct3D device on the machine this backend is developed on, so nothing below can be executed
    /// here. What CAN be executed device-free is every decision that produced the call, because those live in
    /// types with no device in them, and the trace emitter proves them by writing down the calls it would have
    /// made. So the residue this type carries alone is: which Vortice method a resolved call name picks, and the
    /// cast from an <c>object</c> view to its Direct3D type. Decision T3's WARP <c>[GpuFact]</c> is what closes
    /// that last gap, and the 36 goldens on the <c>direct3d11-native</c> leg are what prove the frame.
    /// </para>
    /// <para>
    /// A READONLY STRUCT OVER TWO CLASS REFERENCES, which is the shape <see cref="ID3D11Emitter"/> requires and a
    /// reflection test enforces, and it RECEIVES both rather than constructing either (issue #476). The state is
    /// the device's one cache and the context carries the device's one immediate context plus the scratch arrays
    /// the array calls are made from. An emitter that allocated either would give every command list its own, and
    /// the caches describe what is bound on the CONTEXT rather than what one list recorded.
    /// </para>
    /// <para>
    /// WINDOWS-ONLY AT THE TYPE LEVEL, so the platform-compatibility analyzer gates every use of it at the
    /// device rather than method by method, and no body here is ever JITted on a machine with no Direct3D: the
    /// device that constructs it is Windows-only, and the generic replay is instantiated over this type only from
    /// there. The load-path rule that still binds is the FIELD one, and it is why the scratch arrays live in
    /// <see cref="D3D11EmitterContext"/> and every value-typed call argument is a local.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal readonly partial struct D3D11NativeEmitter : ID3D11Emitter, ID3D11BindSink
    {
        // Every pipeline this backend builds passes the same mask, because the GPU seam has no knob for one. It
        // is a named constant rather than a literal at the call so the OMSetBlendState site reads as "the mask is
        // not a variable" instead of as a magic number.
        const uint FullSampleMask = 0xFFFFFFFFu;

        readonly D3D11DeviceState _state;
        readonly D3D11EmitterContext _context;

        internal D3D11NativeEmitter(D3D11DeviceState state, D3D11EmitterContext context)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>The device-owned state this emitter reads and updates. Every emitter value the device hands
        /// out addresses the same one.</summary>
        internal D3D11DeviceState State => _state;

        /// <summary>The device context and the scratch arrays, likewise one per device.</summary>
        internal D3D11EmitterContext EmitterContext => _context;

        ID3D11DeviceContext1 Native => _context.Context;

        /// <summary>
        /// DECISION R3's SINGLE <c>ClearState</c>, at the head of every replay, and the cache reset immediately
        /// after it so the two agree at the one moment they are guaranteed to.
        /// </summary>
        public void Begin()
        {
            Native.ClearState();
            _state.Reset();
        }

        /// <summary>Closing the scope issues nothing. The end-of-replay completion signal belongs to the submit
        /// (decision C5) and is raised by <see cref="D3D11CommandDrivers"/> around this, not here.</summary>
        public void End()
        {
        }

        /// <summary>
        /// DECISION W6: a framebuffer CHANGE binds the targets and applies the full viewport and the full
        /// scissor, and a redundant re-bind issues NOTHING, viewport and scissor included. The seam has no
        /// <c>SetViewport</c>, so this is the only place a viewport is ever set.
        /// <para>
        /// The surface is resolved BEFORE the identity guard, so a framebuffer from another backend is refused
        /// whether or not the bind was redundant. A guard-first order would let the same mistake pass silently
        /// on the second bind.
        /// </para>
        /// </summary>
        public void SetFramebuffer(IGpuFramebuffer framebuffer)
        {
            ID3D11RenderTargetSurface surface = D3D11BindResolve.RenderTargets(framebuffer);
            if (!_state.BindFramebuffer(framebuffer)) return;

            int count = surface.RenderTargetCount;
            ID3D11RenderTargetView?[] targets = _context.RenderTargets(count);
            for (int i = 0; i < count; i++) targets[i] = (ID3D11RenderTargetView)surface.RenderTargetAt(i);

            Native.OMSetRenderTargets(count, targets!, (ID3D11DepthStencilView?)surface.DepthStencil!);
            _context.ReleaseRenderTargets(count);

            Span<Viewport> viewport = stackalloc Viewport[1];
            viewport[0] = new Viewport(0f, 0f, framebuffer.Width, framebuffer.Height, 0f, 1f);
            Native.RSSetViewports(viewport);
            SetFullScissorRects();
        }

        /// <summary>Clear one colour attachment of the bound framebuffer. Both refusals a stream can earn here (no
        /// framebuffer bound, no attachment at that index) are taken in the shared seam, so the trace emitter
        /// refuses the same stream.</summary>
        public void ClearColorTarget(uint index, KhaozEngine.Primitives.Color rgba)
        {
            ID3D11RenderTargetSurface surface = D3D11BindResolve.RenderTargets(
                D3D11BindResolve.RequireColourAttachment(_state.BoundFramebuffer, index));

            Native.ClearRenderTargetView(
                (ID3D11RenderTargetView)surface.RenderTargetAt((int)index),
                new Color4(rgba.R, rgba.G, rgba.B, rgba.A));
        }

        /// <summary>
        /// Clear the depth attachment. The STENCIL goes with it at zero, matching the incumbent: Veldrid's clear
        /// passed both flags, the seam carries no stencil value to pass instead, and a depth-only view ignores
        /// the stencil flag.
        /// <para>
        /// The shared seam has already refused a framebuffer that DECLARES no depth attachment, so the throw below
        /// is not that rule a second time: it catches a framebuffer of this backend's own that declared one and
        /// carries no view for it, which no stream can produce and both framebuffer types build together.
        /// </para>
        /// </summary>
        public void ClearDepthStencil(float depth)
        {
            ID3D11RenderTargetSurface surface = D3D11BindResolve.RenderTargets(
                D3D11BindResolve.RequireDepthAttachment(_state.BoundFramebuffer));
            var view = (ID3D11DepthStencilView?)surface.DepthStencil ?? throw new InvalidOperationException(
                "The bound framebuffer declares a depth attachment and carries no depth-stencil view for it on "
                + "the native Direct3D 11 backend. Both framebuffer types build the view with the format, so this "
                + "is a defect in this backend's framebuffer construction rather than in the pass that cleared.");

            Native.ClearDepthStencilView(view, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil,
                depth, 0);
        }

        /// <summary>
        /// DECISION R6: one native call per pipeline-level object that ACTUALLY changed, and nothing at all for a
        /// rebind of the pipeline already bound. The pending resource sets are drained FIRST, under the OUTGOING
        /// pipeline's layouts, because those layouts are what number the registers they belong at (R5, rule 5).
        /// <para>
        /// The blend factor and the stencil reference are read from the STATE rather than from the pipeline,
        /// which is not a detour: the state has just adopted them as part of the cache key (issue #454), so
        /// reading them back is what makes "what the cache believes" and "what the call passes" the same value by
        /// construction rather than by two call sites agreeing.
        /// </para>
        /// </summary>
        public void SetPipeline(IGpuPipeline pipeline)
        {
            if (pipeline is null) throw new ArgumentNullException(nameof(pipeline));
            if (pipeline is not D3D11GraphicsPipeline graphics)
                throw new ArgumentException(
                    $"A {pipeline.GetType().Name} reached the native Direct3D 11 emitter as a graphics pipeline. "
                    + "A pipeline this backend created carries the compiled shaders and the four state objects a "
                    + "bind issues, and a pipeline from another backend carries another backend's.",
                    nameof(pipeline));

            D3D11NativeEmitter sink = this;
            _state.Binds.SetGraphicsPipeline(ref sink, pipeline);

            D3D11StateChange changed = _state.BindPipeline(graphics);
            if (changed == D3D11StateChange.None) return;

            if (Has(changed, D3D11StateChange.VertexShader)) Native.VSSetShader(graphics.VertexShader);
            if (Has(changed, D3D11StateChange.PixelShader)) Native.PSSetShader(graphics.PixelShader);
            if (Has(changed, D3D11StateChange.BlendState))
                Native.OMSetBlendState(graphics.BlendState, ToColor4(_state.BoundBlendFactor), FullSampleMask);
            if (Has(changed, D3D11StateChange.DepthStencilState))
                Native.OMSetDepthStencilState(graphics.DepthStencilState, (int)_state.BoundStencilReference);
            if (Has(changed, D3D11StateChange.RasterizerState)) Native.RSSetState(graphics.RasterizerState);
            if (Has(changed, D3D11StateChange.InputLayout)) Native.IASetInputLayout(graphics.InputLayout!);
            if (Has(changed, D3D11StateChange.PrimitiveTopology)) Native.IASetPrimitiveTopology(graphics.Topology);
        }

        /// <summary>
        /// RECORDED, NOT EMITTED. Two streams bound before one draw become ONE
        /// <c>IASetVertexBuffers(0, 2, ...)</c>, which cannot be done after the calls have been made, and the
        /// stride the call needs comes from the pipeline rather than from this bind. The draw issues the batch.
        /// </summary>
        public void SetVertexBuffer(uint slot, IGpuBuffer buffer, uint offsetBytes)
            => _state.Vertices.RecordVertexBuffer(slot, buffer, offsetBytes);

        /// <summary>
        /// Issued at the bind, guarded by the redundancy cache over the pair (buffer, format). There is no array
        /// form of <c>IASetIndexBuffer</c>, so there is nothing to batch it with.
        /// <para>
        /// The buffer is resolved BEFORE the redundancy guard, which is <see cref="SetFramebuffer"/>'s rule on the
        /// other resolve and for the same reason. Guarding first records the buffer, so a foreign one would throw
        /// once and the next identical bind would compare equal against a cache describing a buffer the call never
        /// bound, pass silently, and leave the draw indexing whatever the input assembler still held.
        /// </para>
        /// </summary>
        public void SetIndexBuffer(IGpuBuffer buffer, GpuIndexFormat format)
        {
            ID3D11Buffer native = NativeBuffer(buffer);
            if (!_state.Vertices.BindIndexBuffer(buffer, format)) return;

            Native.IASetIndexBuffer(native, D3D11Formats.ToDxgiFormat(format), 0);
        }

        /// <summary>An explicit scissor, in the <c>RECT</c> form Direct3D takes rather than the seam's
        /// origin-plus-size. Nothing undoes it but another explicit call, a genuine framebuffer CHANGE, or the
        /// <c>ClearState</c> that opens the next replay.</summary>
        public void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
        {
            D3D11BindResolve.RequireSingleScissorRect(index);

            Span<RawRect> rect = stackalloc RawRect[1];
            rect[0] = new RawRect((int)x, (int)y, (int)(x + width), (int)(y + height));
            Native.RSSetScissorRects(rect);
        }

        /// <summary>Reset the scissor to the bound framebuffer's full extent, which is what a framebuffer change
        /// applies and what this restores after an explicit rectangle.</summary>
        public void SetFullScissorRects()
        {
            IGpuFramebuffer framebuffer = _state.BoundFramebuffer ?? throw new InvalidOperationException(
                "SetFullScissorRects was reached with no framebuffer bound on the native Direct3D 11 backend. "
                + "The full scissor IS the bound framebuffer's extent, so there is nothing to reset it to. Bind a "
                + "framebuffer first, which sets the full scissor anyway.");

            Span<RawRect> rect = stackalloc RawRect[1];
            rect[0] = new RawRect(0, 0, (int)framebuffer.Width, (int)framebuffer.Height);
            Native.RSSetScissorRects(rect);
        }

        /// <summary>
        /// DECISION R5, RULE 2, in the order every draw path in this backend takes: the resource-set flush FIRST,
        /// then the batched vertex streams, then the draw. Direct3D 11 has no non-instanced entry point, so the
        /// seam's single-instance draw arrives here as one instance, exactly as the incumbent issued it.
        /// </summary>
        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
        {
            FlushGraphicsBinds();
            FlushVertexBuffers();
            Native.DrawInstanced((int)vertexCount, (int)instanceCount, (int)vertexStart, (int)instanceStart);
        }

        /// <inheritdoc cref="Draw"/>
        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset,
            uint instanceStart)
        {
            FlushGraphicsBinds();
            FlushVertexBuffers();
            Native.DrawIndexedInstanced((int)indexCount, (int)instanceCount, (int)indexStart, vertexOffset,
                (int)instanceStart);
        }

        /// <summary>Decision R5, rule 2, on the compute side. Decision C1's SRV-versus-UAV auto-unbind rides
        /// inside the flush, where the bind arrays are assembled (<see cref="D3D11ViewConflicts"/>), so this path
        /// is the draw path's twin and neither emitter has a compute special case.</summary>
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            FlushComputeBinds();
            Native.Dispatch((int)groupCountX, (int)groupCountY, (int)groupCountZ);
        }

        /// <summary>THE PRE-COMMAND HOOK of decision R5, called first by every draw. Named rather than inlined at
        /// each site so the position is one thing to get right and this draw path reads the same as the trace
        /// emitter's.</summary>
        internal void FlushGraphicsBinds()
        {
            D3D11NativeEmitter sink = this;
            _state.Binds.FlushGraphics(ref sink);
        }

        /// <inheritdoc cref="FlushGraphicsBinds"/>
        internal void FlushComputeBinds()
        {
            D3D11NativeEmitter sink = this;
            _state.Binds.FlushCompute(ref sink);
        }

        /// <summary>
        /// THE BATCHED VERTEX FLUSH (5.3): ONE <c>IASetVertexBuffers</c> over the contiguous span of dirty slots,
        /// or nothing when no stream changed. A clean slot swept in between two dirty ones is rebound to exactly
        /// what it already holds, which is what keeps the law at one call per draw rather than one per stream.
        /// </summary>
        internal void FlushVertexBuffers()
        {
            if (!_state.Vertices.TakeFlush(out uint startSlot, out int count)) return;

            IssueVertexBuffers(startSlot, count);
        }

        /// <summary>
        /// DECISION R8, and NOT a seam member. A resource being disposed is the one moment a cache can be left
        /// naming an object that no longer exists, so the device calls this from its resource disposal: the state
        /// forgets the resource wherever it was cached, and exactly those slots are unbound. A wholesale
        /// <c>ClearState</c> would also be correct and is rejected, since it makes the next draw rebind
        /// everything to drop one object.
        /// </summary>
        internal void ScrubDisposed(object resource)
        {
            D3D11StateChange scrubbed = _state.Scrub(resource, out uint vertexStart, out int vertexCount);
            if (scrubbed == D3D11StateChange.None) return;

            if (Has(scrubbed, D3D11StateChange.VertexShader)) Native.VSSetShader(null!);
            if (Has(scrubbed, D3D11StateChange.PixelShader)) Native.PSSetShader(null!);
            if (Has(scrubbed, D3D11StateChange.BlendState))
                Native.OMSetBlendState(null!, ToColor4(D3D11DeviceState.ClearedBlendFactor), FullSampleMask);
            if (Has(scrubbed, D3D11StateChange.DepthStencilState)) Native.OMSetDepthStencilState(null!, 0);
            if (Has(scrubbed, D3D11StateChange.RasterizerState)) Native.RSSetState(null!);
            if (Has(scrubbed, D3D11StateChange.InputLayout)) Native.IASetInputLayout(null!);
            if (Has(scrubbed, D3D11StateChange.VertexBuffers)) IssueVertexBuffers(vertexStart, vertexCount);
            if (Has(scrubbed, D3D11StateChange.IndexBuffer))
                Native.IASetIndexBuffer(null!, Vortice.DXGI.Format.Unknown, 0);
            if (Has(scrubbed, D3D11StateChange.Framebuffer)) UnsetRenderTargets();
        }

        // ONE IASetVertexBuffers OVER A SPAN, CARRYING WHAT THE RECORD HOLDS. Both callers write the record and
        // neither invents an argument, which is what makes a span that sweeps in a slot the caller never touched
        // safe: the flush rebinds a clean slot to what it already holds, and the scrub has already nulled the
        // records of the slots it forgot, so the same write unbinds exactly those and leaves a live slot between
        // them alone. Writing nulls across the scrub's span instead would drop a live stream while the record
        // still called it bound and clean.
        void IssueVertexBuffers(uint startSlot, int count)
        {
            D3D11VertexStreams streams = _state.Vertices;
            ID3D11Buffer?[] buffers = _context.VertexBuffers(count);
            int[] strides = _context.VertexStrides;
            int[] offsets = _context.VertexOffsets;

            for (int i = 0; i < count; i++)
            {
                uint slot = startSlot + (uint)i;
                IGpuBuffer? buffer = streams.BufferAt(slot);
                buffers[i] = buffer is null ? null : NativeBuffer(buffer);
                strides[i] = (int)streams.StrideAt(slot);
                offsets[i] = (int)streams.OffsetAt(slot);
            }

            Native.IASetVertexBuffers((int)startSlot, count, buffers!, strides, offsets);
        }

        // Unbind the output merger entirely. Reached only from a scrub, when the framebuffer's own texture was
        // disposed while it was still bound.
        void UnsetRenderTargets() => Native.OMSetRenderTargets(0, Array.Empty<ID3D11RenderTargetView>(), null!);

        static bool Has(D3D11StateChange changed, D3D11StateChange flag) => (changed & flag) != 0;

        // A local, never a field: a Vortice value-type FIELD would resolve the interop assembly the moment this
        // type is loaded, and the suite loads every type in the package by reflection.
        static Color4 ToColor4(System.Numerics.Vector4 value)
            => new(value.X, value.Y, value.Z, value.W);
    }
}
