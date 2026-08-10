using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// <c>MTLDispatchType</c>, an <c>NSUInteger</c> in the SDK, declared as <c>long</c> here for the same reason
    /// every other Objective-C enum in this folder is (C# does not allow <c>nint</c> as an enum base).
    /// </summary>
    internal enum MTLDispatchType : long
    {
        /// <summary>Dispatches inside one encoder execute one after another. THE ONLY VALUE THIS BACKEND USES
        /// (M-H4), and the reason it needs no dependent-dispatch hazard machinery at all.</summary>
        Serial = 0,

        /// <summary>Dispatches inside one encoder may overlap. Never used here. It is the value that would make a
        /// ping-pong compute chain read stale data with nothing reporting it, which is the shape the seam's rule
        /// 2 exists for on the backends that have it.</summary>
        Concurrent = 1,
    }

    /// <summary>
    /// <c>MTLCommandEncoder</c>, the protocol the three concrete encoder kinds share, with the ONE member that is
    /// common to all of them: <c>-endEncoding</c>.
    /// <para>
    /// ONE TYPE FOR THE SHARED PROTOCOL RATHER THAN THE SAME SELECTOR IN THREE FILES. The concrete kinds diverge
    /// completely (a render encoder's setters have nothing to do with a blit encoder's copies) and their members
    /// land with the rows that emit them: the argument-table setters and the viewport and scissor with rows 12
    /// and 13, the draws, dispatches, copies and mip chain with row 14. What every kind has is that it must be
    /// ended before another can be created, and that is this.
    /// </para>
    /// <para>
    /// IT ARRIVES AUTORELEASED, from one of <c>MTLCommandBuffer</c>'s three factories, and this backend RETAINS
    /// it for the encoder's lifetime rather than relying on a pool that spans the whole recording. See
    /// <c>MetalEncoderSink</c> for why: a pool wide enough to cover a render pass would also cover every
    /// autoreleased object a frame's worth of recording produces, and a pool narrow enough to be a scope would
    /// drain the encoder out from under the pass.
    /// </para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLCommandEncoder(IntPtr Handle)
    {
        /// <summary>True when the buffer would not make one, which is a buffer already in a state it will not
        /// encode into.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>
        /// <c>-endEncoding</c>. Mandatory: a command buffer with an encoder still open cannot be committed, and
        /// a second encoder cannot be created until this has been sent.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void EndEncoding() => ObjCMsgSend.SendVoid(Handle, ObjCRuntime.Sel("endEncoding"));
    }
}
