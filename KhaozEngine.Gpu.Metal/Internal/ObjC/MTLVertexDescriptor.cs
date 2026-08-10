using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// <c>MTLVertexFormat</c>, an <c>NSUInteger</c>. The FOUR members <see cref="GpuVertexElementFormat"/> can
    /// express, plus the invalid zero so the unset value has a name.
    /// <para>
    /// THE OTHER FIFTY ARE DELIBERATELY ABSENT rather than transcribed, which is the rule
    /// <see cref="MTLGPUFamily"/> already states: the list is a statement about what this backend ASKS FOR.
    /// <c>Veldrid.MTL.MTLFormats.VdToMTLVertexFormat</c> maps thirty-odd Veldrid formats because Veldrid's seam
    /// has thirty-odd, and this engine's has four floats and nothing else. A fifth member added to the seam is a
    /// throw in <c>MetalFormats.ToVertexFormat</c>, which is where it should be seen.
    /// </para>
    /// </summary>
    internal enum MTLVertexFormat : ulong
    {
        /// <summary>Not a format. What an attribute descriptor holds before anything writes one.</summary>
        Invalid = 0,

        /// <summary>One 32-bit float.</summary>
        Float = 28,

        /// <summary>Two 32-bit floats.</summary>
        Float2 = 29,

        /// <summary>Three 32-bit floats.</summary>
        Float3 = 30,

        /// <summary>Four 32-bit floats.</summary>
        Float4 = 31,
    }

    /// <summary>
    /// <c>MTLVertexStepFunction</c>, an <c>NSUInteger</c>. The three a buffer layout can take without
    /// tessellation.
    /// <para>
    /// <c>PerPatch</c> AND <c>PerPatchControlPoint</c> ARE ABSENT because they are tessellation-only and the seam
    /// has no tessellation at all. <see cref="Constant"/> IS here even though nothing selects it, because it is
    /// the value a step function holds when a stride is set and a step function is not, and a reader comparing
    /// against the Metal headers should not have to wonder what zero means.
    /// </para>
    /// </summary>
    internal enum MTLVertexStepFunction : ulong
    {
        /// <summary>Fetch the same element for every vertex and instance. Never selected here.</summary>
        Constant = 0,

        /// <summary>Advance per vertex, which is the seam's step rate of 0.</summary>
        PerVertex = 1,

        /// <summary>Advance per instance, which is the seam's step rate of 1 or more.</summary>
        PerInstance = 2,
    }

    /// <summary>
    /// ONE ENTRY OF AN <c>MTLVertexDescriptor</c>'s <c>layouts</c> ARRAY: a vertex buffer slot's stride and how it
    /// advances. Reached only through <see cref="MTLVertexDescriptor.LayoutAt"/> and owned by the descriptor, so
    /// there is nothing here to release.
    /// <para>
    /// A SEPARATE FILE WOULD BE THE FOLDER'S RULE AND IS NOT WORTH IT, which is the carve-out
    /// <c>MTLSamplerState</c> already takes beside its own descriptor. This class has no existence apart from the
    /// descriptor that hands it out by subscript, and its three setters have nothing to say that the descriptor's
    /// own header does not.
    /// </para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLVertexBufferLayoutDescriptor(IntPtr Handle)
    {
        /// <summary>Write the whole slot: bytes between elements, how it advances, and how fast.</summary>
        /// <param name="stride">Bytes between consecutive elements in this buffer.</param>
        /// <param name="stepFunction">Per vertex or per instance.</param>
        /// <param name="stepRate">Elements per step. At least 1, because Metal rejects 0 exactly as the incumbent's
        /// own <c>Math.Max(1, stepRate)</c> says.</param>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Configure(nuint stride, MTLVertexStepFunction stepFunction, nuint stepRate)
        {
            ObjCMsgSend.SendVoidNUInt(Handle, ObjCRuntime.Sel("setStride:"), stride);
            ObjCMsgSend.SendVoidNUInt(Handle, ObjCRuntime.Sel("setStepFunction:"), (nuint)stepFunction);
            ObjCMsgSend.SendVoidNUInt(Handle, ObjCRuntime.Sel("setStepRate:"), stepRate);
        }
    }

    /// <summary>
    /// ONE ENTRY OF AN <c>MTLVertexDescriptor</c>'s <c>attributes</c> ARRAY: which shader attribute reads which
    /// buffer slot at which byte offset. Owned by the descriptor, same as
    /// <see cref="MTLVertexBufferLayoutDescriptor"/> and here for the same reason.
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLVertexAttributeDescriptor(IntPtr Handle)
    {
        /// <summary>Write the whole attribute.</summary>
        /// <param name="format">Component format.</param>
        /// <param name="offsetBytes">Byte offset inside its own buffer slot.</param>
        /// <param name="bufferIndex">The <c>[[buffer(n)]]</c> index the slot is bound at, which on this backend is
        /// M-B2's top-pinned stream index and never the slot ordinal.</param>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Configure(MTLVertexFormat format, nuint offsetBytes, nuint bufferIndex)
        {
            ObjCMsgSend.SendVoidNUInt(Handle, ObjCRuntime.Sel("setFormat:"), (nuint)format);
            ObjCMsgSend.SendVoidNUInt(Handle, ObjCRuntime.Sel("setOffset:"), offsetBytes);
            ObjCMsgSend.SendVoidNUInt(Handle, ObjCRuntime.Sel("setBufferIndex:"), bufferIndex);
        }
    }

    /// <summary>
    /// An <c>MTLVertexDescriptor</c>, created at +1 through <c>alloc</c> plus <c>init</c> and released once the
    /// render pipeline descriptor has taken its copy.
    ///
    /// <para><b>THE RENDER PIPELINE DESCRIPTOR'S <c>vertexDescriptor</c> PROPERTY IS <c>copy</c>, WHICH IS WHY
    /// RELEASING THIS IS SAFE AND NECESSARY.</b> Setting it hands Metal a snapshot, so the pipeline does not read
    /// this object again and holding it would be a leak per pipeline.</para>
    ///
    /// <para><b>ONE IS CREATED AND SET RATHER THAN THE DESCRIPTOR'S IMPLICIT ONE BEING MUTATED, and that is a
    /// deliberate divergence from the incumbent.</b> <c>Veldrid.MTL.MTLPipeline</c> reads
    /// <c>mtlDesc.vertexDescriptor</c> and writes into whatever comes back, which relies on that property being
    /// non-nil on a freshly allocated descriptor. Apple documents its default as nil. The incumbent ships and
    /// renders, so the implementation evidently hands back a lazily created one, but that is an observation about
    /// an implementation rather than a documented contract, and a nil there would not fail loudly: it would drop
    /// every vertex attribute and fail pipeline creation with a message about the vertex function's inputs,
    /// pointing at the shader. Creating one costs an alloc per pipeline at load time and removes the
    /// question.</para>
    ///
    /// <para><b>WRITE-ONLY, like every other descriptor in this folder.</b> Nothing reads a descriptor back, so
    /// there are no getters to keep in step with the setters.</para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLVertexDescriptor(IntPtr Handle)
    {
        /// <summary>True when the descriptor could not be created.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>A fresh descriptor at +1, or nil.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MTLVertexDescriptor New()
        {
            IntPtr cls = ObjCRuntime.ClassNamed("MTLVertexDescriptor");
            if (cls == IntPtr.Zero) return new MTLVertexDescriptor(IntPtr.Zero);

            IntPtr allocated = ObjCMsgSend.Send(cls, ObjCRuntime.Sel("alloc"));
            return new MTLVertexDescriptor(ObjCMsgSend.Send(allocated, ObjCRuntime.Sel("init")));
        }

        /// <summary>The buffer-layout entry for one <c>[[buffer(n)]]</c> index. Autoreleased and owned by this
        /// descriptor.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal MTLVertexBufferLayoutDescriptor LayoutAt(nuint bufferIndex)
            => new(ObjCMsgSend.SendPtrNUInt(
                ObjCMsgSend.Send(Handle, ObjCRuntime.Sel("layouts")),
                ObjCRuntime.Sel("objectAtIndexedSubscript:"), bufferIndex));

        /// <summary>The attribute entry for one shader attribute index. Autoreleased and owned by this
        /// descriptor.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal MTLVertexAttributeDescriptor AttributeAt(nuint attributeIndex)
            => new(ObjCMsgSend.SendPtrNUInt(
                ObjCMsgSend.Send(Handle, ObjCRuntime.Sel("attributes")),
                ObjCRuntime.Sel("objectAtIndexedSubscript:"), attributeIndex));

        /// <summary>Release this descriptor.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Release()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(Handle);
        }
    }
}
