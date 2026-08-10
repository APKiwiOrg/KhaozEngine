using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// AN <c>MTLRenderCommandEncoder</c>, with the two members the PASS row emits: the viewport and the scissor
    /// rectangle.
    ///
    /// <para><b>THE REST OF THIS PROTOCOL LANDS WITH THE ROWS THAT CALL IT</b>, which is the rule
    /// <c>MetalEncoderSink</c> states and the reason this file is short: a native prototype added by a row with no
    /// caller and no test that runs it is an Objective-C declaration nobody has ever executed, and a wrong ABI
    /// assumption in interop is a memory corruption rather than a compile error. The argument-table setters arrive
    /// with the bind flush (https://github.com/APKiwiOrg/KhaozEngine/issues/579), the draws with row 14
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580), and the pipeline-state block with row 11
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/577).</para>
    ///
    /// <para><b><c>-endEncoding</c> IS NOT HERE, deliberately.</b> It belongs to the protocol all three kinds
    /// share and lives once on <see cref="MTLCommandEncoder"/>, which is what
    /// <see cref="MetalEncoderScope"/> drives every transition through.</para>
    ///
    /// <para><b>BOTH SETTERS ARE THE PLURAL FORM, UNCONDITIONALLY, AT A COUNT OF 1 (M-A7).</b> The incumbent's
    /// <c>FlushViewports</c> picks between <c>setViewports:count:</c> and the singular setter on
    /// <c>IsSupported(macOS_GPUFamily1_v3)</c>, which is a deprecated-feature-set read on the hot path to choose
    /// between two calls that do the same thing at count 1. The seam has no multi-viewport concept, so the count
    /// is always 1 and one code path is the answer. Taking the plural also removes an ABI question rather than
    /// adding one: the singular forms pass their structs BY VALUE, which is the indirect-composite path row 1's
    /// spike had to measure, where these pass an array ADDRESS and a count, two plain register arguments.</para>
    ///
    /// <para><b>NEITHER IS AN <see cref="IMetalEncoderSink"/> CALL, and that is M-T2's line rather than an
    /// oversight.</b> A viewport and a scissor are emitted once per framebuffer change and once per encoder
    /// boundary, so nothing about them scales with draw count, and freezing a budget marginal over them would
    /// gate on a figure nobody should gate on. They are emitted through <see cref="IMetalRenderApi"/> instead,
    /// which exists so section 7.3's three assertions can run with no Metal at all.</para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLRenderCommandEncoder(IntPtr Handle)
    {
        /// <summary>True when the command buffer would not make one, which is M-W5's orphan-target case on a
        /// framebuffer whose drawable came back nil.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>
        /// <c>-setViewports:count:</c> with one viewport.
        /// <para>
        /// THE ADDRESS IS TAKEN OF A LOCAL AND THE CALL DOES NOT OUTLIVE IT. Metal copies the array's contents
        /// during the call, which is what makes a stack address legal here: an encoder holds the viewport as its
        /// own state afterwards and never reads the caller's memory again.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal unsafe void SetViewport(in MTLViewport viewport)
        {
            MTLViewport value = viewport;
            ObjCMsgSend.SendVoidPtrNUInt(Handle, ObjCRuntime.Sel("setViewports:count:"), &value, 1);
        }

        /// <summary><c>-setScissorRects:count:</c> with one rectangle, same shape and same lifetime argument as
        /// <see cref="SetViewport"/>.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal unsafe void SetScissorRect(in MTLScissorRect rect)
        {
            MTLScissorRect value = rect;
            ObjCMsgSend.SendVoidPtrNUInt(Handle, ObjCRuntime.Sel("setScissorRects:count:"), &value, 1);
        }
    }
}
