using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// An <c>MTLRenderPipelineDescriptor</c>, created at +1 through <c>alloc</c> plus <c>init</c> and released as
    /// soon as the pipeline state exists. Write-only, like every other descriptor in this folder.
    ///
    /// <para><b>THIS IS THE ONE DESCRIPTOR METAL VALIDATES AGAINST A COMPILED FUNCTION</b>, which is what makes
    /// pipeline creation the moment the vertex layouts, the attachment formats and the shader all have to agree.
    /// A vertex attribute the function does not declare, an attachment format the fragment function does not
    /// write, or a sample count the target does not have are all rejected HERE, with an <c>NSError</c>, rather
    /// than at the draw. That is the one thing this backend gets for free from the API, so the failure is
    /// reported with Metal's own words rather than paraphrased.</para>
    ///
    /// <para><b>WHAT IS DELIBERATELY NOT SET.</b> <c>alphaToCoverageEnabled</c>, because the GPU seam has no
    /// member for it and <c>VeldridGpuDevice</c> (deleted in 18.0.0) constructed its
    /// <c>BlendStateDescription</c> through the overload that left it false, so the incumbent set false onto a
    /// descriptor whose default is already false. <c>rasterizationEnabled</c>, <c>inputPrimitiveTopology</c> and
    /// the tessellation properties, for the same reason in each case: no seam member reaches them and the
    /// incumbent never wrote one.</para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLRenderPipelineDescriptor(IntPtr Handle)
    {
        /// <summary>True when the descriptor could not be created.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>A fresh descriptor at +1, or nil.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MTLRenderPipelineDescriptor New()
        {
            IntPtr cls = ObjCRuntime.ClassNamed("MTLRenderPipelineDescriptor");
            if (cls == IntPtr.Zero) return new MTLRenderPipelineDescriptor(IntPtr.Zero);

            IntPtr allocated = ObjCMsgSend.Send(cls, ObjCRuntime.Sel("alloc"));
            return new MTLRenderPipelineDescriptor(ObjCMsgSend.Send(allocated, ObjCRuntime.Sel("init")));
        }

        /// <summary>Set the vertex entry point.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetVertexFunction(MTLFunction function)
            => ObjCMsgSend.SendVoidPtr(Handle, ObjCRuntime.Sel("setVertexFunction:"), function.Handle);

        /// <summary>Set the fragment entry point.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetFragmentFunction(MTLFunction function)
            => ObjCMsgSend.SendVoidPtr(Handle, ObjCRuntime.Sel("setFragmentFunction:"), function.Handle);

        /// <summary>Set the vertex input layout. The property is <c>copy</c>, so the caller still owns and must
        /// release the descriptor it passes.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetVertexDescriptor(MTLVertexDescriptor vertex)
            => ObjCMsgSend.SendVoidPtr(Handle, ObjCRuntime.Sel("setVertexDescriptor:"), vertex.Handle);

        /// <summary>
        /// Set the MSAA sample count.
        /// <para>
        /// WRITTEN ONLY WHEN IT IS ABOVE 1, which reproduces the incumbent's own conditional. The default is 1 and
        /// writing 1 changes nothing, so the two paths agree either way, and the conditional is kept because it is
        /// the shape a reader comparing the two files will look for.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetSampleCount(nuint sampleCount)
            => ObjCMsgSend.SendVoidNUInt(Handle, ObjCRuntime.Sel("setSampleCount:"), sampleCount);

        /// <summary>Set the depth attachment's pixel format, which is what makes the pipeline compatible with a
        /// framebuffer that has a depth target.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetDepthAttachmentPixelFormat(MTLPixelFormat format)
            => ObjCMsgSend.SendVoidNUInt(Handle, ObjCRuntime.Sel("setDepthAttachmentPixelFormat:"), (nuint)format);

        /// <summary>Set the stencil attachment's pixel format, which the incumbent wrote only for a combined
        /// depth-stencil format and this backend does the same.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetStencilAttachmentPixelFormat(MTLPixelFormat format)
            => ObjCMsgSend.SendVoidNUInt(Handle, ObjCRuntime.Sel("setStencilAttachmentPixelFormat:"),
                (nuint)format);

        /// <summary>The colour attachment entry at one index. Autoreleased and owned by this descriptor.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal MTLRenderPipelineColorAttachmentDescriptor ColorAttachmentAt(nuint index)
            => new(ObjCMsgSend.SendPtrNUInt(
                ObjCMsgSend.Send(Handle, ObjCRuntime.Sel("colorAttachments")),
                ObjCRuntime.Sel("objectAtIndexedSubscript:"), index));

        /// <summary>Release this descriptor.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Release()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(Handle);
        }
    }

    /// <summary>
    /// An <c>MTLRenderPipelineState</c> handle, the immutable object a descriptor produces. Arrives at +1 from
    /// <c>-newRenderPipelineStateWithDescriptor:error:</c> and is released once by its wrapper.
    /// <para>
    /// BESIDE ITS DESCRIPTOR RATHER THAN IN ITS OWN FILE, which is the carve-out <c>MTLSamplerState</c> already
    /// takes for the same shape: this class has exactly one member and the descriptor that makes it is the whole
    /// of its context.
    /// </para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLRenderPipelineState(IntPtr Handle)
    {
        /// <summary>True when the device would not make a pipeline state, which always comes with an
        /// <c>NSError</c>.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>Release this pipeline state.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Release()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(Handle);
        }
    }
}
