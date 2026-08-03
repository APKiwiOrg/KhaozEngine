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
    /// NOTHING HERE BLOCKS. <see cref="CompletedValue"/> is a poll and returns whatever the GPU has reached, and
    /// there is no wait member at all, because the seam's fence contract is explicitly a poll and the one caller
    /// that wants to block (<c>IGpuDevice.WaitForIdle</c>) builds its own spin out of these two members inside
    /// <c>D3D11FenceSubsystem</c>. A blocking member here would be reached by
    /// <see cref="IGpuFence.Signaled"/>, which must never block.
    /// </para>
    /// <para>
    /// THREADING: no implementation is thread-safe on its own and none needs to be. Every call is made under the
    /// device's single submit lock (decision W4), which is also what makes the event-query fallback legal at all,
    /// since its poll runs on the immediate context. <c>D3D11FenceSubsystem</c> is the one type that takes
    /// that lock, so nothing else in the backend has to think about it.
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
        /// The highest value the GPU has reached, as a NON-BLOCKING poll. Monotonic: it never goes backwards, and
        /// it may lag reality (a completed signal reads as completed no earlier than the next poll, never later).
        /// </summary>
        ulong CompletedValue { get; }
    }
}
