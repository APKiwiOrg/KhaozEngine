using System.Collections.Generic;
using System.Text;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// What <see cref="MetalTimelineProbe"/> measured against a real device, as numbers rather than as a pass.
    /// The transcript is printed by the test whichever way the assertions go, because a green assertion records
    /// nothing and the whole point of running this on hardware is what the hardware answered.
    /// </summary>
    internal sealed class MetalTimelineProbeResult
    {
        internal bool DeviceCreated { get; init; }
        internal bool QueueCreated { get; init; }
        internal bool SharedEventCreated { get; init; }

        /// <summary>
        /// Whether <c>MTLCreateSystemDefaultDevice</c> handed back the SAME pointer when called twice, which is
        /// the measurement that decides what the completion registry keys on. True means the default device is a
        /// per-GPU process singleton, so two engine devices on one GPU are indistinguishable by
        /// <c>MTLDevice</c> pointer and the routing table has to key on something unique per engine device.
        /// </summary>
        internal bool DefaultDeviceIsProcessSingleton { get; init; }

        /// <summary>
        /// Two queues on ONE device each registered their own latch and both registrations were accepted, which
        /// is the routing key's whole point measured on real Metal. Keyed on the <c>MTLDevice</c> this would be
        /// false, because the second call would hit the registered-twice refusal.
        /// </summary>
        internal bool TwoQueuesOnOneDeviceBothRegistered { get; init; }

        /// <summary>The value the first submission was allocated and encoded. 1 on a fresh timeline.</summary>
        internal ulong FirstValue { get; init; }

        /// <summary>The value the second submission was allocated. 2, which is what makes the re-armed fence's
        /// target strictly higher than the one it just held.</summary>
        internal ulong SecondValue { get; init; }

        /// <summary>A fence straight from the timeline, before anything armed it. False is the seam's
        /// requirement that a fence is unsignalled when it is submitted.</summary>
        internal bool FenceSignaledBeforeArming { get; init; }

        /// <summary><c>signaledValue</c> read off the real event after the first drain returned.</summary>
        internal ulong SignaledAfterFirstDrain { get; init; }

        /// <summary>The armed fence after the first drain. This is a REAL GPU completion reaching a seam
        /// fence.</summary>
        internal bool FenceSignaledAfterFirstDrain { get; init; }

        /// <summary>The same fence after <c>Reset</c>. False, because the target went back to unarmed.</summary>
        internal bool FenceSignaledAfterReset { get; init; }

        /// <summary><c>signaledValue</c> after the second submission drained.</summary>
        internal ulong SignaledAfterSecondDrain { get; init; }

        /// <summary>The re-armed fence after the second drain, which is the whole of <c>Reset</c> being usable
        /// rather than merely present.</summary>
        internal bool FenceSignaledAfterSecondDrain { get; init; }

        /// <summary>How many completion handlers fired for the two submitted buffers. Polled with a deadline
        /// rather than read once, because the handler carries no ordering responsibility and Metal delivers it on
        /// its own thread whenever it likes.</summary>
        internal int CompletionsSeen { get; init; }

        /// <summary>Every completion reported <c>MTLCommandBufferStatus.Completed</c>.</summary>
        internal bool AllCompletionsCompleted { get; init; }

        /// <summary>Every completion reported a nil <c>error</c>. The load-bearing half of M-G4's read: the
        /// latch's own path is exercised, and on a healthy device it reports nothing.</summary>
        internal bool AllCompletionsErrorFree { get; init; }

        /// <summary>The first status any completion reported, for the transcript.</summary>
        internal nint FirstCompletionStatus { get; init; }

        /// <summary>Drains counted by the liveness experiment's timeline. 1: a wait that really blocked on a
        /// value the GPU was never going to signal.</summary>
        internal long CountedDrains { get; init; }

        /// <summary>Milliseconds that drain spent blocked, which is what <c>GpuDeviceCounters.DrainMs</c>
        /// carries.</summary>
        internal double CountedDrainMs { get; init; }

        /// <summary>That the sliced drain returned once liveness flipped underneath it, derived from the DRAIN
        /// rather than from the flipper thread: <c>WaitForIdle</c> returned, and the timeline never reached the
        /// value it was waiting for, and the slice loop has no third exit. A false here is a hang rather than a
        /// failure, so it is recorded for the transcript and asserted by the test.</summary>
        internal bool DrainReleasedByDeviceDeath { get; init; }

        /// <summary>The fence's answer once the device is dead, which must be true whatever it was armed with
        /// (M-F6).</summary>
        internal bool FenceSignaledAfterDeviceDeath { get; init; }

        /// <summary>Anything the probe could not measure, in the order it gave up on it.</summary>
        internal IReadOnlyList<string> Notes { get; init; } = new List<string>();

        /// <summary>The transcript, for the test to print.</summary>
        internal string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("device created: " + DeviceCreated);
            sb.AppendLine("queue created: " + QueueCreated);
            sb.AppendLine("MTLSharedEvent created: " + SharedEventCreated);
            sb.AppendLine("MTLCreateSystemDefaultDevice twice, same pointer: "
                + DefaultDeviceIsProcessSingleton);
            sb.AppendLine("two queues on one device both registered a latch: "
                + TwoQueuesOnOneDeviceBothRegistered);
            sb.AppendLine("first submission value: " + FirstValue);
            sb.AppendLine("second submission value: " + SecondValue);
            sb.AppendLine("fence signaled before arming: " + FenceSignaledBeforeArming);
            sb.AppendLine("signaledValue after first drain: " + SignaledAfterFirstDrain);
            sb.AppendLine("fence signaled after first drain: " + FenceSignaledAfterFirstDrain);
            sb.AppendLine("fence signaled after Reset: " + FenceSignaledAfterReset);
            sb.AppendLine("signaledValue after second drain: " + SignaledAfterSecondDrain);
            sb.AppendLine("fence signaled after second drain: " + FenceSignaledAfterSecondDrain);
            sb.AppendLine("completion handlers seen: " + CompletionsSeen);
            sb.AppendLine("first completion status: " + FirstCompletionStatus + " (4 = Completed)");
            sb.AppendLine("all completions Completed: " + AllCompletionsCompleted);
            sb.AppendLine("all completions error-free: " + AllCompletionsErrorFree);
            sb.AppendLine("counted drains: " + CountedDrains);
            sb.AppendLine("counted drain ms: " + CountedDrainMs.ToString("0.###"));
            sb.AppendLine("drain released by device death: " + DrainReleasedByDeviceDeath);
            sb.AppendLine("fence signaled after device death: " + FenceSignaledAfterDeviceDeath);

            if (Notes.Count == 0) return sb.ToString();

            sb.AppendLine("notes:");
            foreach (string note in Notes) sb.AppendLine("  - " + note);
            return sb.ToString();
        }
    }
}
