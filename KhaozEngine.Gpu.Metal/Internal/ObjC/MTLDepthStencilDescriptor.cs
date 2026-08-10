using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// <c>MTLCompareFunction</c>, an <c>NSUInteger</c>. The full set, and the map in <c>MetalFormats</c> is total
    /// over <see cref="GpuComparison"/>, so a complete table reads better than a subset a reader has to trust.
    /// </summary>
    internal enum MTLCompareFunction : ulong
    {
        /// <summary>Never passes.</summary>
        Never = 0,

        /// <summary>Passes if less.</summary>
        Less = 1,

        /// <summary>Passes if equal.</summary>
        Equal = 2,

        /// <summary>Passes if less or equal, which is the 3D model pass's depth test.</summary>
        LessEqual = 3,

        /// <summary>Passes if greater.</summary>
        Greater = 4,

        /// <summary>Passes if not equal.</summary>
        NotEqual = 5,

        /// <summary>Passes if greater or equal.</summary>
        GreaterEqual = 6,

        /// <summary>Always passes, which is what a depth-test-off pipeline declares.</summary>
        Always = 7,
    }

    /// <summary>
    /// An <c>MTLDepthStencilDescriptor</c>, created at +1 through <c>alloc</c> plus <c>init</c> and released once
    /// the state exists.
    ///
    /// <para><b>THE DEPTH TEST ENABLE IS NOT A FIELD HERE, AND THAT IS METAL RATHER THAN AN OMISSION.</b> There is
    /// no <c>depthTestEnabled</c> on this descriptor: a disabled depth test IS
    /// <see cref="MTLCompareFunction.Always"/> with writes off, which is exactly what the incumbent's own
    /// <c>DepthStencilStateDescription</c> resolves to before it reaches Metal. <c>MetalPipelineState</c> is where
    /// that resolution is written down, so the seam's three-field depth state has one home rather than being
    /// half-applied here.</para>
    ///
    /// <para><b>THE STENCIL HALF IS ABSENT because the GPU seam has no stencil state at all.</b>
    /// <c>Veldrid.MTL.MTLPipeline</c> builds two <c>MTLStencilDescriptor</c>s behind
    /// <c>if (description.DepthStencilState.StencilTestEnabled)</c>, and no engine call site can make that
    /// condition true: <see cref="GpuDepthStencilState"/> carries a test flag, a write flag and a comparison, and
    /// nothing else. So the branch is not reproduced, the stencil reference stays 0, and this paragraph is the
    /// citation for both. The day the seam grows stencil state, this descriptor grows the two members with
    /// it.</para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLDepthStencilDescriptor(IntPtr Handle)
    {
        /// <summary>True when the descriptor could not be created.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>A fresh descriptor at +1, or nil.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MTLDepthStencilDescriptor New()
        {
            IntPtr cls = ObjCRuntime.ClassNamed("MTLDepthStencilDescriptor");
            if (cls == IntPtr.Zero) return new MTLDepthStencilDescriptor(IntPtr.Zero);

            IntPtr allocated = ObjCMsgSend.Send(cls, ObjCRuntime.Sel("alloc"));
            return new MTLDepthStencilDescriptor(ObjCMsgSend.Send(allocated, ObjCRuntime.Sel("init")));
        }

        /// <summary>Write the whole depth state: the comparison and whether passing fragments write depth.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Configure(MTLCompareFunction comparison, bool depthWriteEnabled)
        {
            ObjCMsgSend.SendVoidNUInt(Handle, ObjCRuntime.Sel("setDepthCompareFunction:"), (nuint)comparison);
            ObjCMsgSend.SendVoidBool(Handle, ObjCRuntime.Sel("setDepthWriteEnabled:"),
                depthWriteEnabled ? (byte)1 : (byte)0);
        }

        /// <summary>Release this descriptor.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Release()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(Handle);
        }
    }

    /// <summary>
    /// An <c>MTLDepthStencilState</c> handle, the immutable object a descriptor produces. Arrives at +1 from
    /// <c>-newDepthStencilStateWithDescriptor:</c> and is released once by its wrapper. Beside its descriptor for
    /// <c>MTLSamplerState</c>'s reason.
    ///
    /// <para><b>A PIPELINE WITH NO DEPTH ATTACHMENT HOLDS A NIL ONE, deliberately, and that nil is the depth-target
    /// guard's other half.</b> The incumbent creates this object only inside
    /// <c>if (outputs.DepthAttachment != null)</c>, so a colour-only pipeline's <c>DepthStencilState</c> is
    /// default-constructed and nil, and its <c>PreDrawCommand</c> then guards the emission on the FRAMEBUFFER
    /// having a depth target. Both halves are reproduced: <c>MetalGraphicsPipeline</c> creates one only for a
    /// pipeline whose outputs declare depth, and the emission stays gated on the bound framebuffer, because
    /// <c>-setDepthStencilState:</c> on a pass with no depth attachment is a validation error under the debug
    /// layer M-T7 arms on every native-leg run.</para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLDepthStencilState(IntPtr Handle)
    {
        /// <summary>True when there is no state, which is the colour-only pipeline's case rather than a
        /// failure.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>Release this state.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Release()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(Handle);
        }
    }
}
