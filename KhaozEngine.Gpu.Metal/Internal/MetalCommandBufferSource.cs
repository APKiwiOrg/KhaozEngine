using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE REAL <see cref="IMetalCommandBufferSource"/>: <c>-[MTLCommandQueue commandBuffer]</c>, retained.
    ///
    /// <para><b>THE RETAIN IS THE WHOLE OF WHY THIS TYPE EXISTS RATHER THAN A CALL AT THE LIST.</b>
    /// <c>-commandBuffer</c> hands back an AUTORELEASED object, so it dies with whatever pool was in scope on the
    /// recording thread, and a command list holds its buffer across every call the consumer makes between
    /// <c>Begin</c> and the submit, which is an unbounded stretch of the consumer's own code. Retaining at
    /// acquisition and releasing at exactly one of the list's three exits is what makes that lifetime the LIST's,
    /// and pairing the two here is what keeps them from drifting apart.</para>
    ///
    /// <para><b>ONE QUEUE, AND IT IS THE DEVICE'S ONE QUEUE (M-N2).</b> Metal documents
    /// <c>MTLCommandQueue</c> as thread-safe, which is what lets N lists on N threads each take their own buffer
    /// with no lock here at all. What IS serialised is the commit, under the device's submit lock, because
    /// <c>-commit</c> enqueues and a queue executes in enqueue order, so committing under one lock is what makes
    /// SUBMIT order the observable order the GPU seam documents.</para>
    ///
    /// <para><b>THERE IS DELIBERATELY NO <c>-enqueue</c> AT ACQUISITION.</b> Enqueuing at <c>Begin</c> would fix
    /// a buffer's place in the execution order at the moment it was taken rather than at the moment it was
    /// submitted, which is not what the seam says, and it would let submits proceed without the lock.</para>
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal sealed class MetalCommandBufferSource : IMetalCommandBufferSource
    {
        readonly MTLCommandQueue _queue;

        /// <param name="queue">The device's one queue (M-N2).</param>
        internal MetalCommandBufferSource(MTLCommandQueue queue) => _queue = queue;

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public IntPtr Acquire()
        {
            // ITS OWN POOL rather than the caller's, so a list can be begun from any thread without the caller
            // having to know that -commandBuffer autoreleases. The retain below is what survives the pop.
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            MTLCommandBuffer buffer = _queue.CommandBuffer();
            if (buffer.IsNull) return IntPtr.Zero;

            return ObjCRuntime.ObjcRetain(buffer.Handle);
        }

        /// <inheritdoc/>
        /// <remarks>The release of the retain <see cref="Acquire"/> took, and nothing else. A buffer that was
        /// COMMITTED is retained by the queue until it completes, so this is never the last reference to one the
        /// GPU is still running.</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Release(IntPtr commandBuffer)
        {
            if (commandBuffer == IntPtr.Zero) return;

            ObjCRuntime.ObjcRelease(commandBuffer);
        }
    }
}
