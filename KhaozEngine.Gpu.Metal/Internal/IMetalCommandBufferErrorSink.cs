namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// <c>MTLCommandBufferStatus</c>, by number, for the two values the completion path compares against. The
    /// enum itself belongs to row 4's <c>Internal/ObjC/</c> layer
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/570) and declaring a partial copy of one here would be
    /// the start of a second, which is the same call row 1's spike and row 2's probe both made.
    /// </summary>
    internal static class MetalCommandBufferStatus
    {
        /// <summary>The buffer finished, and its encoded signal has been reached.</summary>
        internal const nint Completed = 4;

        /// <summary>The buffer failed. <c>error</c> is non-nil and carries the reason (M-G4).</summary>
        internal const nint Error = 5;
    }

    /// <summary>
    /// WHAT A COMMAND BUFFER'S COMPLETION SAID, copied out of the Objective-C objects while they are still
    /// alive, so everything that DECIDES anything about it is device-free.
    /// <para>
    /// The description is a managed string taken from <c>NSError.localizedDescription</c> at the fault site,
    /// because the <c>NSError</c> is autoreleased and dies with the pool the handler pushed. A sink that stored
    /// the pointer instead would be holding freed memory by the time anyone read it, and a telemetry header
    /// written minutes later is exactly that case.
    /// </para>
    /// </summary>
    internal readonly struct MetalCommandBufferOutcome
    {
        internal MetalCommandBufferOutcome(nint status, nint errorCode, string errorDescription)
        {
            Status = status;
            ErrorCode = errorCode;
            ErrorDescription = errorDescription;
        }

        /// <summary>The buffer's <c>status</c> at completion. See <see cref="MetalCommandBufferStatus"/>.</summary>
        internal nint Status { get; }

        /// <summary>The <c>MTLCommandBufferError</c> code, or 0 when there was no error.</summary>
        internal nint ErrorCode { get; }

        /// <summary>The error's <c>localizedDescription</c>, or the empty string when there was no error.</summary>
        internal string ErrorDescription { get; }

        /// <summary>True when this buffer failed, which is the whole of what a latch acts on.</summary>
        internal bool Failed => Status == MetalCommandBufferStatus.Error;
    }

    /// <summary>
    /// WHERE A COMPLETED COMMAND BUFFER'S <c>status</c> AND <c>error</c> GO (M-G4), and the seam row 4's error
    /// latch implements (https://github.com/APKiwiOrg/KhaozEngine/issues/570).
    /// <para>
    /// THE HANDLER OWNS REPORTING AND THE SHARED EVENT OWNS ORDERING, which is M-F2's ruling and the reason this
    /// interface has exactly one member with no return value. A completion handler is delivered on an arbitrary
    /// internal thread in no guaranteed order, so anything that inferred an ORDER from the sequence of calls
    /// here would be depending on a fact Metal does not promise. Nothing does: the timeline answers every
    /// ordering question and this answers none.
    /// </para>
    /// <para>
    /// WHY THE HANDLER EXISTS AT ALL, since the shared event replaced the fence dictionary. The incumbent read
    /// <c>status</c> in exactly one place (to decide whether to wait) and never reads <c>error</c>, so a Metal
    /// command-buffer failure is invisible to the engine and to telemetry today, which is what #427 asks for.
    /// M-G4 requires both to be read at completion in EVERY configuration, so the handler survives the fence
    /// rewrite with reporting as its only job. Phase 3's lesson applies to the implementation on the other side
    /// of this interface: a latch built on checks that compile away in Release never fires, so it must not be
    /// <c>[Conditional("DEBUG")]</c>.
    /// </para>
    /// </summary>
    internal interface IMetalCommandBufferErrorSink
    {
        /// <summary>
        /// A command buffer belonging to this sink's device completed. Called from Metal's completion thread,
        /// once per submitted buffer, whatever the outcome.
        /// <para>
        /// IT MUST NOT THROW. The call arrives across the Objective-C boundary, where an escaping exception
        /// terminates the process rather than unwinding to anything that could report it.
        /// <see cref="MetalCompletionHandler"/> catches anyway, because a rule enforced only by convention is
        /// the one that fails in the field, but a sink that relies on that catch is reporting nothing.
        /// </para>
        /// </summary>
        /// <param name="outcome">What the buffer's <c>status</c> and <c>error</c> said, copied out.</param>
        void CommandBufferCompleted(in MetalCommandBufferOutcome outcome);
    }
}
