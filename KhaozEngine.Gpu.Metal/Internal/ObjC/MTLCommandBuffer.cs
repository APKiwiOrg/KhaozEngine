using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// <c>MTLCommandBufferStatus</c>. An <c>NSInteger</c>, which is 64 bits everywhere Metal ships, so the
    /// underlying type is <c>long</c> (C# does not allow <c>nint</c> as an enum base).
    /// <para>
    /// The incumbent reads this in exactly ONE place, <c>WaitForIdleCore</c>, to decide whether waiting is worth
    /// it. Decision M-G4 reads it at every completion instead, because <see cref="Error"/> is the only place a
    /// Metal device loss is ever reported and this is the flag that says an error is there to read.
    /// </para>
    /// </summary>
    internal enum MTLCommandBufferStatus : long
    {
        /// <summary>Created and not yet enqueued.</summary>
        NotEnqueued = 0,

        /// <summary>Enqueued, so its place in the queue's execution order is fixed.</summary>
        Enqueued = 1,

        /// <summary>Committed and waiting on the scheduler.</summary>
        Committed = 2,

        /// <summary>Scheduled: its dependencies are met and the GPU has it.</summary>
        Scheduled = 3,

        /// <summary>Finished successfully. The only status that is not a reason to look at
        /// <see cref="MTLCommandBuffer.Error"/>.</summary>
        Completed = 4,

        /// <summary>Failed. <see cref="MTLCommandBuffer.Error"/> carries an <c>NSError</c> whose code is an
        /// <see cref="MTLCommandBufferError"/>.</summary>
        Error = 5,
    }

    /// <summary>
    /// <c>MTLCommandBufferError</c>, the code on an <c>NSError</c> in the <c>MTLCommandBufferErrorDomain</c>. An
    /// <c>NSInteger</c>, so <c>long</c> for the same reason as the status above.
    /// <para>
    /// Transcribed for the CODES THIS BACKEND REPORTS rather than exhaustively, and the point of naming them at
    /// all is that the latch's header field carries a stable token a capture can group on across sessions. An
    /// unlisted code still latches and still reports, as its number.
    /// </para>
    /// </summary>
    internal enum MTLCommandBufferError : long
    {
        /// <summary>No error. What a completed command buffer's nil <c>NSError</c> reads as.</summary>
        None = 0,

        /// <summary>An internal driver error.</summary>
        Internal = 1,

        /// <summary>The command buffer took too long and the system killed it. One of the two codes that names a
        /// hung GPU rather than a bad command.</summary>
        Timeout = 2,

        /// <summary>A page fault the GPU could not service.</summary>
        PageFault = 3,

        /// <summary>Access to the device was revoked, which is what a process loses when the system takes the GPU
        /// away from it.</summary>
        AccessRevoked = 4,

        /// <summary>The process is not permitted to run this work.</summary>
        NotPermitted = 7,

        /// <summary>Out of memory. Notable because it is the one common code that is about the WORKLOAD rather
        /// than about the device.</summary>
        OutOfMemory = 8,

        /// <summary>A resource referenced by the command buffer was invalid.</summary>
        InvalidResource = 9,

        /// <summary>A memoryless render target ran out of tile memory.</summary>
        Memoryless = 10,

        /// <summary>The device was physically removed. The eGPU case, and the clearest device loss Metal
        /// reports.</summary>
        DeviceRemoved = 11,

        /// <summary>A shader overflowed its stack.</summary>
        StackOverflow = 12,
    }

    /// <summary>
    /// An <c>MTLCommandBuffer</c> handle, with the members this row needs: commit, a blocking wait, and the
    /// status and error pair decision M-G4's latch is built on.
    /// <para>
    /// IT ARRIVES AUTORELEASED, from <see cref="MTLCommandQueue.CommandBuffer"/>, so every caller is inside an
    /// <see cref="ObjCAutoreleasePool"/> scope. Nothing here releases one by hand and nothing should: releasing
    /// an autoreleased object is an over-release, which is a crash somewhere else entirely.
    /// </para>
    /// <para>
    /// THE COMPLETION HANDLER IS NOT HERE. <c>-addCompletedHandler:</c> and the <c>[UnmanagedCallersOnly]</c>
    /// block behind it belong to the timeline row
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/571), whose handler's ONLY job is to read
    /// <see cref="Status"/> and <see cref="Error"/> and hand them to this row's latch (M-F2). Row 1's spike
    /// already proved the handler fires from a global block literal with no delegate and no GC handle on the
    /// path, so what row 5 adds is the wiring rather than the answer.
    /// </para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLCommandBuffer(IntPtr Handle)
    {
        /// <summary>True when the queue would not make one, which is a device that is already in trouble.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary><c>-commit</c>. Enqueues the buffer if it was not already enqueued, so commit order IS
        /// execution order when commits are serialised (M-N2).</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Commit() => ObjCMsgSend.SendVoid(Handle, ObjCRuntime.Sel("commit"));

        /// <summary>
        /// <c>-waitUntilCompleted</c>: block this thread until the GPU has finished this buffer AND everything
        /// committed to the queue before it, because a queue executes in enqueue order.
        /// <para>
        /// That property is the whole of what makes <c>MetalQueueDrain</c> a real drain without a shared
        /// event, which is why this row can honour M-F6's "drain BEFORE teardown" while the timeline is still
        /// another row's work.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void WaitUntilCompleted()
            => ObjCMsgSend.SendVoid(Handle, ObjCRuntime.Sel("waitUntilCompleted"));

        /// <summary><c>-status</c>.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal MTLCommandBufferStatus Status()
            => (MTLCommandBufferStatus)ObjCMsgSend.SendNInt(Handle, ObjCRuntime.Sel("status"));

        /// <summary><c>-error</c>, nil on every buffer that did not fail.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal NSError Error() => new(ObjCMsgSend.Send(Handle, ObjCRuntime.Sel("error")));
    }
}
