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
    /// <see cref="ObjCAutoreleasePool"/> scope. A buffer used and committed INSIDE one pool scope needs nothing
    /// else, and calling <see cref="Release"/> on one would be an over-release, which is a crash somewhere else
    /// entirely.
    /// </para>
    /// <para>
    /// <b>A BUFFER HELD ACROSS POOL SCOPES IS THE EXCEPTION, AND IT NEEDS <see cref="Retain"/>.</b> The setup
    /// command buffer of M-M9 accumulates uploads from separate calls and commits once, so it outlives the pool
    /// the queue handed it out in, and the pop of that pool would release it: the next append would then message
    /// a freed object. That is a use-after-free rather than a leak, and it is the failure this pair exists to
    /// prevent. The rule is the ordinary Objective-C one, written down because the surrounding rule is the
    /// opposite: whoever holds an autoreleased object past its pool retains it, and releases it once when done.
    /// </para>
    /// <para>
    /// THE THREE ENCODER FACTORIES ARE HERE AND THEY ALL HAND BACK AUTORELEASED OBJECTS TOO. Exactly one encoder
    /// may be open on a buffer at a time, which is Metal's rule rather than a policy this backend invents, and
    /// <c>MetalEncoderScope</c> is the one type that owns the transitions. These members do not enforce that: a
    /// handle type reproduces the API and the state machine sits above it.
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

        /// <summary>Retain this buffer, for a holder that keeps it past the autorelease pool it was created in.
        /// See the type summary: the only holder that does is the device's setup command buffer.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Retain()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRetain(Handle);
        }

        /// <summary>Release a buffer that was <see cref="Retain"/>ed. Never called on one that was not: an
        /// autoreleased object released by hand is an over-release.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Release()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(Handle);
        }

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

        /// <summary>
        /// <c>-renderCommandEncoderWithDescriptor:</c>. The ONE native call the deferred begin is (M-A1), taking
        /// the <c>MTLRenderPassDescriptor</c> that carries the attachments, the folded clears and the explicit
        /// store actions. Nil when Metal will not encode into this buffer, which M-W5's orphan-target rule is the
        /// answer to rather than a throw.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal IntPtr RenderCommandEncoder(IntPtr descriptor)
            => ObjCMsgSend.SendPtr(Handle, ObjCRuntime.Sel("renderCommandEncoderWithDescriptor:"), descriptor);

        /// <summary>
        /// <c>-blitCommandEncoder</c>: a new blit encoder, AUTORELEASED like the buffer that made it.
        /// <para>
        /// TWO CALLERS AND THEY WANT DIFFERENT THINGS FROM IT, which is why it hands back the typed handle rather
        /// than a raw pointer. The device-owned setup buffer opens one, records its copy and ends it in a single
        /// call (M-M9, <c>MetalSetupCommands</c>), so it wants <see cref="MTLBlitCommandEncoder"/>'s copy member.
        /// A command list opens one through <c>MetalEncoderSink</c> and holds it across calls under the
        /// one-encoder-at-a-time invariant, so it wants the bare handle and takes <c>.Handle</c>. One member
        /// serves both, because the difference is what the caller does with the encoder and not what
        /// <c>-blitCommandEncoder</c> returns.
        /// </para>
        /// <para>
        /// THE KIND WHOSE COST SECTION 2.1 IS ABOUT: opening one ENDS a render encoder, so a record-time upload
        /// that takes this path costs the next draw a full re-activation.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal MTLBlitCommandEncoder BlitCommandEncoder()
            => new(ObjCMsgSend.Send(Handle, ObjCRuntime.Sel("blitCommandEncoder")));

        /// <summary>
        /// <c>-computeCommandEncoderWithDispatchType:</c> with <see cref="MTLDispatchType.Serial"/> (M-H4).
        /// <para>
        /// SERIAL IS WHAT MAKES THIS BACKEND NEED NO DEPENDENT-DISPATCH HAZARD MACHINERY: dispatches inside one
        /// serial encoder do not overlap, so a dispatch that reads what an earlier one wrote is ordered without a
        /// barrier. The dispatch type is passed rather than defaulted because
        /// <c>-computeCommandEncoder</c> is the concurrent-capable form on some paths and the difference is
        /// invisible until a chain reads stale data.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal IntPtr ComputeCommandEncoder(MTLDispatchType dispatchType)
            => ObjCMsgSend.SendPtrNInt(Handle, ObjCRuntime.Sel("computeCommandEncoderWithDispatchType:"),
                (nint)dispatchType);
    }
}
