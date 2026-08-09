using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// An <c>MTLCommandQueue</c> handle. ONE per device (M-N2), created once and released at teardown.
    /// <para>
    /// THE QUEUE IS DOCUMENTED THREAD-SAFE, and that is what makes M-W8's lock-free recording true. Command
    /// buffers execute in ENQUEUE order, and <c>-commit</c> enqueues if the buffer was not already enqueued, so
    /// committing under the device's submit lock makes SUBMIT ORDER the observable order by construction, which
    /// is exactly what the GPU seam documents. There is deliberately no <c>-enqueue</c> call at <c>Begin</c>: it
    /// would let submits proceed without the lock and it would make the order depend on <c>Begin</c> rather than
    /// on <c>Submit</c>, which is not what the seam says.
    /// </para>
    /// <para>
    /// NO SECOND QUEUE AND NO ASYNC COMPUTE. #534's argument transfers with the FFT ocean as the same named
    /// consumer: a second queue needs cross-queue <c>MTLSharedEvent</c> signalling and its own submit lock, for a
    /// renderer whose uploads are megabytes at load time and whose compute is one chain already gated by the
    /// seam's rule 2.
    /// </para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLCommandQueue(IntPtr Handle)
    {
        /// <summary>True when there is no queue, which is what <c>-newCommandQueue</c> answers on a device that
        /// has run out of them.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>Release this queue. Only ever called on a handle that arrived at +1 from
        /// <c>-newCommandQueue</c>.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Release()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(Handle);
        }

        /// <summary>
        /// <c>-commandBuffer</c>: a new command buffer, AUTORELEASED. That is the single most important
        /// ownership fact in this backend, because it is the object created most often: without a pool in scope
        /// on the recording thread it accumulates for the life of the process, one per frame, which is precisely
        /// the shape decision M-N5 exists to rule out.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal MTLCommandBuffer CommandBuffer()
            => new(ObjCMsgSend.Send(Handle, ObjCRuntime.Sel("commandBuffer")));
    }
}
