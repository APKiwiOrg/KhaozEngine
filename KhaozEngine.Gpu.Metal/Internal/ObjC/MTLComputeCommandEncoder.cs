using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// AN <c>MTLComputeCommandEncoder</c>, with the four argument-table setters the bind flush emits into it
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/579).
    ///
    /// <para><b>A SEPARATE FILE BECAUSE IT IS A SEPARATE PROTOCOL, and the setters are not the same selectors
    /// with a different receiver.</b> A compute encoder has ONE stage, so its selectors carry no stage word at
    /// all: <c>setBuffers:offsets:withRange:</c> where the render encoder has
    /// <c>setVertexBuffers:offsets:withRange:</c> and <c>setFragmentBuffers:offsets:withRange:</c>. The
    /// SIGNATURES are identical, which is why both types send through the same three prototypes on
    /// <see cref="ObjCMsgSend"/>, and the folder's one-file-per-class rule is what keeps the two selector sets
    /// from being spelled in one switch that has to be right about three protocols at once.</para>
    ///
    /// <para><b>EVERY SELECTOR ARRIVED WITH THE ROW THAT CALLS IT.</b> Both
    /// <c>setComputePipelineState:</c> and <c>dispatchThreadgroups:threadsPerThreadgroup:</c> are row 14's
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580), including the pipeline-state one that row 11's own
    /// cell predicted: it goes into a COMPUTE ENCODER, and under M-A1's deferred begin there is no encoder at the
    /// moment <c>SetComputePipeline</c> is called, which is the same correction 6.3 records for the graphics
    /// block. A prototype with no caller and no test that runs it is an Objective-C declaration nobody has ever
    /// executed, and a wrong ABI assumption in interop is a memory corruption rather than a compile error.</para>
    ///
    /// <para><b><c>-endEncoding</c> IS NOT HERE</b>, for the reason <see cref="MTLRenderCommandEncoder"/> gives:
    /// it belongs to the protocol all three kinds share, lives once on <see cref="MTLCommandEncoder"/>, and
    /// <see cref="MetalEncoderScope"/> is the one owner of every transition through it.</para>
    ///
    /// <para><b>THE ENCODER IS OPENED WITH THE SERIAL DISPATCH TYPE (M-H4)</b>, by
    /// <c>MetalEncoderSink.BeginComputeEncoder</c> rather than here, which is what makes dispatches inside one
    /// encoder ordered with no hazard machinery behind them. Nothing on this type depends on that, and it is
    /// worth knowing while reading a bind into a compute table anyway.</para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLComputeCommandEncoder(IntPtr Handle)
    {
        /// <summary>True when the command buffer would not make one, which is a device already in trouble rather
        /// than the orphan-target case a nil RENDER encoder is.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>
        /// <c>-setBuffers:offsets:withRange:</c>, over a CONTIGUOUS run of the compute buffer table starting at
        /// <paramref name="firstIndex"/> (M-R6). Both spans are pinned for the call and not beyond it: Metal
        /// copies them during the call and the encoder holds the bindings as its own state afterwards.
        /// </summary>
        /// <param name="buffers">The <c>MTLBuffer</c> objects, one per index in the run. A nil entry unbinds its
        /// index.</param>
        /// <param name="offsets">The composed byte offset for each, same length and same order.</param>
        /// <param name="firstIndex">The run's first argument-table index.</param>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal unsafe void SetBuffers(ReadOnlySpan<IntPtr> buffers, ReadOnlySpan<nuint> offsets, uint firstIndex)
        {
            fixed (IntPtr* objects = buffers)
            fixed (nuint* offsetValues = offsets)
            {
                ObjCMsgSend.SendVoidBuffersRange(Handle, ObjCRuntime.Sel("setBuffers:offsets:withRange:"),
                    objects, offsetValues, new NSRange(firstIndex, (nuint)buffers.Length));
            }
        }

        /// <summary><c>-setTextures:withRange:</c>. Same run and same lifetime rule, with no offsets array
        /// because the texture table binds no window.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal unsafe void SetTextures(ReadOnlySpan<IntPtr> textures, uint firstIndex)
        {
            fixed (IntPtr* objects = textures)
            {
                ObjCMsgSend.SendVoidObjectsRange(Handle, ObjCRuntime.Sel("setTextures:withRange:"), objects,
                    new NSRange(firstIndex, (nuint)textures.Length));
            }
        }

        /// <summary><c>-setSamplerStates:withRange:</c>, the two-argument form rather than the one carrying LOD
        /// clamps, for <see cref="MTLRenderCommandEncoder.SetSamplerStates"/>'s reason.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal unsafe void SetSamplerStates(ReadOnlySpan<IntPtr> samplers, uint firstIndex)
        {
            fixed (IntPtr* objects = samplers)
            {
                ObjCMsgSend.SendVoidObjectsRange(Handle, ObjCRuntime.Sel("setSamplerStates:withRange:"), objects,
                    new NSRange(firstIndex, (nuint)samplers.Length));
            }
        }

        /// <summary><c>-setBufferOffset:atIndex:</c> (M-R7), which moves an EXISTING binding's window without
        /// rewriting the argument-table entry behind it. A buffer must already be bound at
        /// <paramref name="index"/>, which the flush guarantees by only reaching this arm for a set it already
        /// wrote into this encoder's table.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetBufferOffset(nuint offset, uint index)
            => ObjCMsgSend.SendVoidNUIntNUInt(Handle, ObjCRuntime.Sel("setBufferOffset:atIndex:"), offset, index);

        /// <summary><c>-setComputePipelineState:</c>, the compute half of the pipeline-state block. One call
        /// rather than the render encoder's five to eight, because a compute pipeline has no rasterizer state, no
        /// blend colour and no depth-stencil state to go with it.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetComputePipelineState(MTLComputePipelineState state)
            => ObjCMsgSend.SendVoidPtr(Handle, ObjCRuntime.Sel("setComputePipelineState:"), state.Handle);

        /// <summary>
        /// <c>-dispatchThreadgroups:threadsPerThreadgroup:</c>.
        /// <para>
        /// THE GROUP SIZE IS AN ARGUMENT HERE WHERE BOTH SIBLINGS READ IT OUT OF THE COMPILED MODULE, which is
        /// why <c>MetalComputePipeline</c> carries the workgroup size at all: row 9 reads it out of the SPIR-V
        /// (<c>SpirvLocalSize</c>) rather than taking it from a description nothing validates, and it travels from
        /// the shader to this call.
        /// </para>
        /// <para>
        /// THE ENCODER IS SERIAL (M-H4), which is what makes two dispatches inside it ordered with no barrier
        /// machinery, and is the whole of why this backend has none. That is set at
        /// <c>-computeCommandEncoderWithDispatchType:</c> and not here.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void DispatchThreadgroups(MTLSize threadgroupsPerGrid, MTLSize threadsPerThreadgroup)
            => ObjCMsgSend.SendVoidDispatchThreadgroups(Handle,
                ObjCRuntime.Sel("dispatchThreadgroups:threadsPerThreadgroup:"),
                threadgroupsPerGrid, threadsPerThreadgroup);
    }
}
