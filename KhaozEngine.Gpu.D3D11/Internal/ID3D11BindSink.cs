using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// One entry of a constant-buffer array bind: the buffer, and the window inside it expressed the way
    /// <c>*SetConstantBuffers1</c> takes it, in 16-byte CONSTANTS rather than bytes.
    /// <para>
    /// A DEFAULT VALUE IS A HOLE, and it is a legal one. An array bind covers a contiguous register span, and a
    /// span may contain a register this set does not make visible to this stage. Direct3D 11 requires a null
    /// buffer to carry a first constant and a constant count of zero, which is exactly what the default value is,
    /// so a hole needs no special case at the call site.
    /// </para>
    /// </summary>
    internal readonly struct D3D11ConstantBufferBind
    {
        internal D3D11ConstantBufferBind(IGpuBuffer? buffer, uint firstConstant, uint constantCount)
        {
            Buffer = buffer;
            FirstConstant = firstConstant;
            ConstantCount = constantCount;
        }

        /// <summary>The buffer, or null for a hole in the span.</summary>
        internal IGpuBuffer? Buffer { get; }

        /// <summary>The window start in constants, with the ring's per-frame base, the set's resolved range offset
        /// and the per-draw dynamic offset already summed (see <see cref="D3D11ConstantRange"/>).</summary>
        internal uint FirstConstant { get; }

        /// <summary>The window length in constants, after the 256-byte minimum.</summary>
        internal uint ConstantCount { get; }
    }

    /// <summary>
    /// WHERE THE BIND FLUSH PUTS ITS NATIVE CALLS, and the seam that makes decision T2's budget a device-free
    /// <c>[Fact]</c> over the SHIPPED fan-out rather than over a copy of it.
    /// <para>
    /// THE SINK DECISION OF WORK-BREAKDOWN ROW 9, since this interface is what it produced. The question was where
    /// the countable native-call sink goes, and the two shapes on the table were a sink BELOW the real emitter
    /// (tallying the shipped fan-out, no drift, at the cost of a second generic parameter) or a device-free
    /// harness that reproduces the fan-out and the guards (cheap, and structurally able to drift). The replay row
    /// changed the terms by moving every redundancy and viewport guard into the device-owned
    /// <see cref="D3D11DeviceState"/>, so a third shape became available and is what shipped: the SCHEDULE and the
    /// FAN-OUT live in device-owned, device-free types (<see cref="D3D11BindFlush"/> and
    /// <see cref="D3D11SetActivation"/>) that decide which calls to make, and this seam is the only thing that
    /// differs between the real emitter and <see cref="D3D11NativeTraceEmitter"/>. What a budget test taken over
    /// the trace emitter can drift in is therefore the NAMING TRANSLATION alone (which stage's method name a
    /// register file plus a stage picks, which is <see cref="D3D11NativeCallName"/> and nothing else), never the
    /// dirty tracking, the slot order, the drain, the dedup, the register arithmetic or the batching. Decision
    /// T3's WARP <c>[GpuFact]</c> is a belt-and-braces check on that one translation rather than the only thing
    /// standing between the budget and reality.
    /// </para>
    /// <para>
    /// CONSUMED THROUGH A GENERIC CONSTRAINT (<c>where TSink : struct, ID3D11BindSink</c>) and never through the
    /// interface, for the same reason <see cref="ID3D11Emitter"/> is: the JIT monomorphizes each implementation,
    /// so the flush carries no interface dispatch and boxes nothing. An implementation is a readonly struct whose
    /// state lives behind a class reference, which is the emitter's rule and holds here because both shipped
    /// implementations ARE emitters.
    /// </para>
    /// <para>
    /// EVERY MEMBER IS AN ARRAY CALL, which is decision R6 expressed as a type. There is deliberately no
    /// single-slot overload of anything: the law is one native call per register file per stage per flush, so a
    /// per-element entry point would be the #418 fan-out defect available as an API. A span covers a CONTIGUOUS
    /// register range starting at <c>startSlot</c>, holes included.
    /// </para>
    /// <para>
    /// THERE IS NO <c>Create*</c> MEMBER HERE EITHER, for the same reason there is none on the emitter seam
    /// (decision X1). Every view a bind names was created at resource or set creation.
    /// </para>
    /// </summary>
    internal interface ID3D11BindSink
    {
        /// <summary>
        /// <c>*SetConstantBuffers1</c> for one stage, over the contiguous register span starting at
        /// <paramref name="startSlot"/>. ALWAYS the <c>1</c> overload with explicit first constants and constant
        /// counts (decision R7), including a bind of a buffer's full range, because the uniform ring's per-frame
        /// base is always an addend and a plain <c>*SetConstantBuffers</c> has nowhere to put it.
        /// </summary>
        void SetConstantBuffers(GpuShaderStages stage, uint startSlot, ReadOnlySpan<D3D11ConstantBufferBind> binds);

        /// <summary>
        /// THE <c>!DriverCommandLists</c> WORKAROUND (decision R7): unbind the same span immediately before the
        /// <see cref="SetConstantBuffers"/> that follows it, issued only when the driver reports that the D3D11
        /// runtime is EMULATING command lists. On that path a re-bind of the same buffer at a different first
        /// constant is dropped, so the offset silently does not move and every draw after the first reads the
        /// first draw's constants. Unbinding first makes the following bind a genuine change.
        /// <para>
        /// It doubles the constant-buffer call count on an emulated-command-list driver, which is the cost of
        /// being correct there, and it is why both driver arms are asserted rather than just the fast one.
        /// </para>
        /// </summary>
        void UnsetConstantBuffers(GpuShaderStages stage, uint startSlot, int count);

        /// <summary>
        /// <c>*SetShaderResources</c> for one stage. Sampled textures and read-only structured buffers share the
        /// <c>t</c> file, so one span may carry both.
        /// </summary>
        void SetShaderResources(GpuShaderStages stage, uint startSlot, ReadOnlySpan<IGpuBindableResource?> resources);

        /// <summary><c>*SetSamplers</c> for one stage.</summary>
        void SetSamplers(GpuShaderStages stage, uint startSlot, ReadOnlySpan<IGpuBindableResource?> samplers);

        /// <summary>
        /// <c>CSSetUnorderedAccessViews</c>. Read-write structured buffers and storage textures share the
        /// <c>u</c> file. Compute is the only stage this reaches on Direct3D 11: a pixel-shader UAV is bound
        /// through <c>OMSetRenderTargetsAndUnorderedAccessViews</c> alongside the render targets rather than
        /// through a stage setter, and no shipped layout declares a <c>u</c> binding outside compute.
        /// </summary>
        void SetUnorderedAccessViews(GpuShaderStages stage, uint startSlot, ReadOnlySpan<IGpuBindableResource?> views);
    }
}
