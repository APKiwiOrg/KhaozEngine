using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>Which mechanism is carrying this device's completion timeline (decision C5). Reported so a
    /// session log names it: the two paths give the same answers, so a run that silently took the fallback would
    /// otherwise be indistinguishable from one that did not, and the fallback has a different cost profile.</summary>
    internal enum D3D11FenceMechanism
    {
        /// <summary>The primary path: one device-wide <c>ID3D11Fence</c> created through <c>ID3D11Device5</c> and
        /// signalled with <c>ID3D11DeviceContext4.Signal</c>. Needs Windows 10 1703 or newer.</summary>
        MonotonicFence = 0,

        /// <summary>The fallback for a runtime with no <c>ID3D11Device5</c>: a pool of <c>ID3D11Query</c> event
        /// queries, one per signal, polled with <c>DO_NOT_FLUSH</c> and retired in submission order.</summary>
        EventQuery = 1,
    }

    /// <summary>
    /// THE DEVICE-WIDE COMPLETION TIMELINE the whole fence subsystem is built on: a monotonically increasing
    /// counter that the CPU advances at a point in the submission stream and the GPU walks up as it finishes the
    /// work before each point. Everything the seam calls a fence is a REMEMBERED VALUE on this one timeline
    /// (see <c>D3D11GpuFence</c>), never a device object of its own.
    /// <para>
    /// ONE timeline per device, and it is the reason decision C5 works at all. Veldrid's Direct3D 11 fence is a
    /// <c>ManualResetEvent</c> set the instant <c>ExecuteCommandList</c> returns, so it is a submit RECEIPT and
    /// not a completion signal, which is why the incumbent backend hardcodes
    /// <see cref="GpuCapabilities.SupportsCompletionFences"/> false. A counter the GPU advances is a completion
    /// signal, on both mechanisms below, which is what lets that capability read true here.
    /// </para>
    /// <para>
    /// ONE MEMBER BLOCKS, AND ONLY THE DRAIN MAY REACH IT. <see cref="CompletedValue"/> is a poll and returns
    /// whatever the GPU has reached, which is what <see cref="IGpuFence.Signaled"/> is built on and why that path
    /// never waits. <see cref="TryWaitForValue"/> is the single exception and exists because the alternative was
    /// worse: a drain built out of the poll alone escalates to a millisecond sleep, and one such sleep costs more
    /// than the entire per-frame drain budget the drain is measured against (M2, 0.2 ms). It is called by
    /// <c>D3D11FenceSubsystem.WaitForIdle</c> and by nothing else, so nothing on the seam's fence path can reach
    /// a wait.
    /// </para>
    /// <para>
    /// THREADING: no implementation is thread-safe on its own, and there are exactly two members it does not have
    /// to be thread-safe for. Everything else is called under the device's single submit lock (decision W4),
    /// which is what makes the event-query fallback legal at all, since its poll and its signal both run on the
    /// immediate context. The exceptions are <see cref="CompletedValue"/> on a mechanism that reports
    /// <see cref="PollIsFreeThreaded"/>, and <see cref="TryWaitForValue"/>, which is never called under the lock
    /// because a wait holding it would deadlock against the submission that would release it.
    /// <c>D3D11FenceSubsystem</c> is the one type that takes that lock, so nothing else in the backend has to
    /// think about it.
    /// </para>
    /// <para>
    /// An implementation is device-facing by nature, so both shipped ones are Windows-only and the device-free
    /// tests drive a fake through this interface instead. That split is deliberate: the ORDERING, the target
    /// lifecycle, the drain loop and the kill switch are engine logic and are tested on every operating system,
    /// and what is left behind this interface is the two native calls per mechanism.
    /// </para>
    /// </summary>
    internal interface ID3D11FenceTimeline : IDisposable
    {
        /// <summary>Which of the two mechanisms this is. For the session log, not for behaviour: a caller that
        /// branches on this has found a difference the timeline was supposed to hide.</summary>
        D3D11FenceMechanism Mechanism { get; }

        /// <summary>
        /// Whether <see cref="CompletedValue"/> is safe to read WITHOUT the device's submit lock.
        /// <para>
        /// True on a mechanism whose completion object is free-threaded, which is the monotonic
        /// <c>ID3D11Fence</c>: <c>GetCompletedValue</c> is a read on the fence object and touches no context.
        /// False on the event-query fallback, whose poll runs on the IMMEDIATE CONTEXT and therefore has to be
        /// serialised against submission like everything else that touches it.
        /// </para>
        /// <para>
        /// This is the one place the two mechanisms are genuinely not interchangeable, and it is a difference in
        /// what a poll COSTS rather than in what it answers. It exists because the seam documents that a fence
        /// poll never waits, and under W4 the submit lock covers a whole replay, so a poll that took the lock
        /// could wait for one. A caller may not branch on it for anything else.
        /// </para>
        /// </summary>
        bool PollIsFreeThreaded { get; }

        /// <summary>
        /// Place a signal at the current point in the submission stream and return its value. Values are strictly
        /// increasing across the life of the timeline, so a value returned later covers everything an earlier one
        /// covered.
        /// <para>
        /// Called once at the end of every replay (decision C5) and once more by each real
        /// <c>WaitForIdle</c> drain. Cheap enough to pay on every submit, which is what keeps the timeline dense
        /// enough for a fence handed to <c>Submit</c> to mean exactly that submission.
        /// </para>
        /// </summary>
        ulong Signal();

        /// <summary>
        /// Hand everything recorded so far to the GPU, so a signal already placed is a point the GPU will
        /// actually reach.
        /// <para>
        /// THE DRAIN CALLS THIS EXACTLY ONCE, after placing its own signal and before its first poll, and nothing
        /// else calls it at all. The immediate context buffers commands, so a signal sitting at the tail of a
        /// buffer the driver has not been handed yet is a point the GPU may never arrive at, and a drain polling
        /// for it spins with nothing on the other end. Polling is not what fixes that: the event-query poll is
        /// deliberately <c>DO_NOT_FLUSH</c>, which Direct3D documents as able to loop forever for exactly this
        /// reason, and the monotonic fence has no flushing poll at all.
        /// </para>
        /// <para>
        /// IT IS NOT ON THE FENCE POLL PATH, deliberately. <see cref="IGpuFence.Signaled"/> stays non-flushing,
        /// because a poll that flushed would turn every look at a fence into a submission and give the seam's
        /// cheapest member a cost that grows with how often a consumer looks. The drain is the one caller that
        /// has decided to wait, so it is the one caller that may pay for the work to be handed over.
        /// </para>
        /// <para>Called under the submit lock, as any context call must be.</para>
        /// </summary>
        void Flush();

        /// <summary>
        /// The highest value the GPU has reached, as a NON-BLOCKING poll. Monotonic: it never goes backwards, and
        /// it may lag reality (a completed signal reads as completed no earlier than the next poll, never later).
        /// </summary>
        ulong CompletedValue { get; }

        /// <summary>
        /// Block the calling thread until the GPU reaches <paramref name="value"/> or
        /// <paramref name="timeoutMilliseconds"/> elapses, and report whether this timeline HAS such a wait at
        /// all.
        /// <para>
        /// TRUE means the wait was carried out, whether the value was reached or the slice merely elapsed, and
        /// the caller re-polls either way. FALSE means this mechanism has no blocking wait, or could not arm one,
        /// and the caller must spin instead. The event-query fallback answers false always, because Direct3D
        /// offers no blocking wait on an event query and one built out of the immediate context would need the
        /// submit lock held across it.
        /// </para>
        /// <para>
        /// THE ONE MEMBER CALLED WITHOUT THE SUBMIT LOCK. Waiting under that lock deadlocks against the very
        /// submission that would let the wait finish, so a mechanism may only answer true here when its wait is
        /// free-threaded. The monotonic fence's is: the wait is armed on the fence object rather than on the
        /// context.
        /// </para>
        /// <para>
        /// WHY A TIMEOUT AT ALL, when the drain deliberately has none. The slice is not a drain timeout and never
        /// shortens a wait for work that is still coming. It is how often an unsatisfied wait comes back so the
        /// caller can re-check device liveness, which is the drain's only escape from a GPU that has hung.
        /// </para>
        /// </summary>
        /// <param name="value">The timeline value to wait for.</param>
        /// <param name="timeoutMilliseconds">How long this one wait slice may last.</param>
        /// <returns>True when this timeline waited, false when it has no blocking wait to offer.</returns>
        bool TryWaitForValue(ulong value, int timeoutMilliseconds);
    }
}
