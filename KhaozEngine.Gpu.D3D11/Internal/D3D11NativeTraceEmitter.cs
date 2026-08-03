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
    /// WHAT IT DOES NOT MODEL, stated so no total taken from it is read as a budget. The bind flush of decision
    /// R5 and its array-batched fan-out are work-breakdown row 9, so a resource-set bind lands here as
    /// <see cref="D3D11NativeCall.ResourceSetPending"/>, which holds its place in the order and is not a native
    /// call. Where the countable sink for decision T2's budget finally goes, below the real emitter or into a
    /// harness guarded by T3's WARP <c>[GpuFact]</c>, is still row 9's decision and this type does not take it:
    /// two of T2's four structural invariants are what this row owes (exactly one <c>ClearState</c> per submit,
    /// and one <c>RSSetViewports</c> plus one <c>RSSetScissorRects</c> per framebuffer CHANGE with zero for a
    /// redundant re-bind) and those are what it is built to assert.
    /// </para>
    /// <para>
    /// A readonly struct over two class references, which is the shape <see cref="ID3D11Emitter"/> requires and a
    /// reflection test enforces. It RECEIVES its state rather than constructing one, which is the discipline
    /// issue #476 is about: an emitter that allocated its own would satisfy the readonly rule and still give
    /// every command list its own cache.
    /// </para>
    /// </summary>
    internal readonly struct D3D11NativeTraceEmitter : ID3D11Emitter
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
        /// </summary>
        public void SetFramebuffer(IGpuFramebuffer framebuffer)
        {
            if (framebuffer is null) throw new ArgumentNullException(nameof(framebuffer));
            if (!_state.BindFramebuffer(framebuffer)) return;

            _log.Record(D3D11NativeCall.OMSetRenderTargets, _log.Id(framebuffer));
            _log.Record(D3D11NativeCall.RSSetViewports, FullViewport(framebuffer));
            _log.Record(D3D11NativeCall.RSSetScissorRects, FullScissor(framebuffer));
        }

        public void ClearColorTarget(uint index, Color rgba)
            => _log.Record(D3D11NativeCall.ClearRenderTargetView,
                $"{N(index)},{N(rgba.R)},{N(rgba.G)},{N(rgba.B)},{N(rgba.A)}");

        public void ClearDepthStencil(float depth)
            => _log.Record(D3D11NativeCall.ClearDepthStencilView, N(depth));

        /// <summary>
        /// DECISION R6: one native call per pipeline-level object that ACTUALLY changed, and nothing at all for a
        /// rebind of the pipeline already bound. The calls are issued in cache-slot order, which D3D11 does not
        /// require and a readable trace does.
        /// </summary>
        public void SetPipeline(IGpuPipeline pipeline)
        {
            if (pipeline is null) throw new ArgumentNullException(nameof(pipeline));
            if (pipeline is not ID3D11PipelineState state)
                throw new ArgumentException(
                    $"A {pipeline.GetType().Name} reached the native Direct3D 11 emitter as a graphics pipeline. "
                    + "A pipeline this backend created also implements ID3D11PipelineState, which is what the "
                    + "redundancy caches of decision R6 compare against, so a pipeline without it would rebind "
                    + "all seven state objects on every draw.", nameof(pipeline));

            D3D11StateChange changed = _state.BindPipeline(state);
            if (changed == D3D11StateChange.None) return;

            if (Has(changed, D3D11StateChange.VertexShader))
                _log.Record(D3D11NativeCall.VSSetShader, _log.Id(state.VertexShader));
            if (Has(changed, D3D11StateChange.PixelShader))
                _log.Record(D3D11NativeCall.PSSetShader, _log.Id(state.PixelShader));
            if (Has(changed, D3D11StateChange.BlendState))
                _log.Record(D3D11NativeCall.OMSetBlendState, _log.Id(state.BlendState));
            if (Has(changed, D3D11StateChange.DepthStencilState))
                _log.Record(D3D11NativeCall.OMSetDepthStencilState, _log.Id(state.DepthStencilState));
            if (Has(changed, D3D11StateChange.RasterizerState))
                _log.Record(D3D11NativeCall.RSSetState, _log.Id(state.RasterizerState));
            if (Has(changed, D3D11StateChange.InputLayout))
                _log.Record(D3D11NativeCall.IASetInputLayout, _log.Id(state.InputLayout));
            if (Has(changed, D3D11StateChange.PrimitiveTopology))
                _log.Record(D3D11NativeCall.IASetPrimitiveTopology, N(state.PrimitiveTopology));
        }

        /// <summary>Recorded, not emitted. See <see cref="D3D11NativeCall.ResourceSetPending"/>: the flush is
        /// row 9's.</summary>
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
            => _log.Record(D3D11NativeCall.ResourceSetPending, $"gfx,{N(slot)},{_log.Id(set)}");

        /// <inheritdoc cref="SetGraphicsResourceSet(uint, IGpuResourceSet)"/>
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => _log.Record(D3D11NativeCall.ResourceSetPending,
                $"gfx,{N(slot)},{_log.Id(set)},{N(dynamicOffset)}");

        public void SetVertexBuffer(uint slot, IGpuBuffer buffer, uint offsetBytes)
            => _log.Record(D3D11NativeCall.IASetVertexBuffers,
                $"{N(slot)},1,{_log.Id(buffer)},{N(offsetBytes)}");

        public void SetIndexBuffer(IGpuBuffer buffer, GpuIndexFormat format)
            => _log.Record(D3D11NativeCall.IASetIndexBuffer, $"{_log.Id(buffer)},{format}");

        /// <summary>
        /// An explicit scissor overrides whatever a framebuffer bind left behind, and nothing undoes it: the only
        /// things that touch the scissor afterwards are another explicit call, a genuine framebuffer CHANGE, and
        /// the <c>ClearState</c> that opens the next replay.
        /// <para>
        /// Traced as <c>out&lt;index&gt;:&lt;count&gt;,&lt;left&gt;,&lt;top&gt;,&lt;right&gt;,&lt;bottom&gt;</c>,
        /// which is the D3D11 <c>RECT</c> rather than the seam's origin-plus-size, so a trace can be read
        /// straight against a capture.
        /// </para>
        /// </summary>
        public void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
            => _log.Record(D3D11NativeCall.RSSetScissorRects,
                $"out{N(index)}:1,{N(x)},{N(y)},{N(x + width)},{N(y + height)}");

        public void SetFullScissorRects()
        {
            IGpuFramebuffer framebuffer = _state.BoundFramebuffer ?? throw new InvalidOperationException(
                "SetFullScissorRects was reached with no framebuffer bound on the native Direct3D 11 backend. "
                + "The full scissor IS the bound framebuffer's extent, so there is nothing to reset it to. Bind a "
                + "framebuffer first, which sets the full scissor anyway.");

            _log.Record(D3D11NativeCall.RSSetScissorRects, FullScissor(framebuffer));
        }

        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
            => _log.Record(D3D11NativeCall.DrawInstanced,
                $"{N(vertexCount)},{N(instanceCount)},{N(vertexStart)},{N(instanceStart)}");

        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset,
            uint instanceStart)
            => _log.Record(D3D11NativeCall.DrawIndexedInstanced,
                $"{N(indexCount)},{N(instanceCount)},{N(indexStart)},{N(vertexOffset)},{N(instanceStart)}");

        public void UpdateBuffer(IGpuBuffer buffer, uint offsetBytes, ReadOnlySpan<byte> data)
            => _log.Record(D3D11NativeCall.UpdateSubresource,
                $"{_log.Id(buffer)},{N(offsetBytes)},{N(data.Length)}b");

        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes,
            uint sizeInBytes)
            => _log.Record(D3D11NativeCall.CopySubresourceRegion,
                $"{_log.Id(src)},{N(srcOffsetBytes)},{_log.Id(dst)},{N(dstOffsetBytes)},{N(sizeInBytes)}");

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

        /// <summary>Bound unguarded. A compute pipeline is one shader and gets its own dirty tracking with the
        /// compute schedule of decision C1, which is work-breakdown row 12, so caching it here would be half a
        /// rule.</summary>
        public void SetComputePipeline(IGpuComputePipeline pipeline)
            => _log.Record(D3D11NativeCall.CSSetShader, _log.Id(pipeline));

        /// <inheritdoc cref="SetGraphicsResourceSet(uint, IGpuResourceSet)"/>
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set)
            => _log.Record(D3D11NativeCall.ResourceSetPending, $"cs,{N(slot)},{_log.Id(set)}");

        /// <inheritdoc cref="SetGraphicsResourceSet(uint, IGpuResourceSet)"/>
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => _log.Record(D3D11NativeCall.ResourceSetPending,
                $"cs,{N(slot)},{_log.Id(set)},{N(dynamicOffset)}");

        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
            => _log.Record(D3D11NativeCall.Dispatch,
                $"{N(groupCountX)},{N(groupCountY)},{N(groupCountZ)}");

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
            D3D11StateChange scrubbed = _state.Scrub(resource);
            if (scrubbed == D3D11StateChange.None) return;

            if (Has(scrubbed, D3D11StateChange.VertexShader))
                _log.Record(D3D11NativeCall.VSSetShader, "null");
            if (Has(scrubbed, D3D11StateChange.PixelShader))
                _log.Record(D3D11NativeCall.PSSetShader, "null");
            if (Has(scrubbed, D3D11StateChange.BlendState))
                _log.Record(D3D11NativeCall.OMSetBlendState, "null");
            if (Has(scrubbed, D3D11StateChange.DepthStencilState))
                _log.Record(D3D11NativeCall.OMSetDepthStencilState, "null");
            if (Has(scrubbed, D3D11StateChange.RasterizerState))
                _log.Record(D3D11NativeCall.RSSetState, "null");
            if (Has(scrubbed, D3D11StateChange.InputLayout))
                _log.Record(D3D11NativeCall.IASetInputLayout, "null");
            if (Has(scrubbed, D3D11StateChange.Framebuffer))
                _log.Record(D3D11NativeCall.OMSetRenderTargets, "null");
        }

        static bool Has(D3D11StateChange changed, D3D11StateChange flag) => (changed & flag) != 0;

        // The full viewport a framebuffer change brings: the whole target, depth 0 to 1, exactly what Veldrid's
        // SetFullViewports produces for a single output.
        static string FullViewport(IGpuFramebuffer framebuffer)
            => $"1,0,0,{N(framebuffer.Width)},{N(framebuffer.Height)},0,1";

        // Every output at once, matching SetFullScissorRects, and the same RECT shape as an explicit rect.
        static string FullScissor(IGpuFramebuffer framebuffer)
            => $"all:1,0,0,{N(framebuffer.Width)},{N(framebuffer.Height)}";

        // Invariant culture throughout, so a trace compares equal on a machine whose decimal separator is a
        // comma, matching the emitter call log.
        static string N(uint value) => value.ToString(CultureInfo.InvariantCulture);
        static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
        static string N(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    }
}
