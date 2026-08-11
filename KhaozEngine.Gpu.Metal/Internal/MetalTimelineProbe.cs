using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;

using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE TIMELINE AGAINST A REAL DEVICE, which is the one thing no <c>[Fact]</c> can reach: a value being
    /// signalled because GPU WORK finished rather than because a test set a property.
    /// <para>
    /// Everything the timeline DECIDES is device-free and is driven exhaustively by
    /// <c>MetalTimelineTests</c> over a fake event. What is left underneath is four native calls plus the
    /// completion block, and this is what runs them: <c>newSharedEvent</c>, <c>encodeSignalEvent:value:</c> on a
    /// committed command buffer, <c>signaledValue</c> reading back what the GPU reached,
    /// <c>waitUntilSignaledValue:timeoutMS:</c>, and an <c>[UnmanagedCallersOnly]</c> completion handler
    /// delivering a real <c>status</c> and <c>error</c> to a latch.
    /// </para>
    /// <para>
    /// IT CREATES ITS OWN DEVICE AND QUEUE, because row 4 owns the real ones
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/570) and row 5 is pulled ahead of it: row 8's ring reads
    /// a completion value, so a ring built before the timeline exists is a silent corruption. That is the same
    /// call row 2's probe made against the same row, and the handoff on #570 says row 4 re-points this at the
    /// real device rather than leaving two device-creating paths in the assembly.
    /// </para>
    /// <para>
    /// THE COUNTED DRAIN IS MEASURED WITHOUT GPU WORK, deliberately. A drain that only counts when it BLOCKED is
    /// the seam's own rule, so timing it against a real submission would race the GPU finishing first and the
    /// probe would assert a number it cannot force. The second half of this probe instead targets a value
    /// nothing will ever signal and flips liveness from another thread, which is the exact shape M-G4's error
    /// latch produces in the field, and it makes both the counting and the slice loop's release deterministic on
    /// real hardware.
    /// </para>
    /// </summary>
    internal static class MetalTimelineProbe
    {
        // How long the probe is willing to wait for something the GPU or the driver owns the timing of: the two
        // submissions completing, and their completion handlers being delivered. Generous, because it is a
        // deadline for giving up rather than an expectation.
        const int DeadlineMs = 5000;

        // The value the liveness experiment waits for. Nothing ever encodes it, so the only way out of that
        // drain is the liveness flip, which is what the experiment is for.
        const ulong UnreachableValue = 9_999;

        /// <summary>Run the probe against the system default Metal device. Never throws: anything it cannot
        /// measure comes back as a note, because the point is a transcript rather than a pass.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MetalTimelineProbeResult Run()
        {
            var notes = new List<string>();
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            try
            {
                return RunInsidePool(notes);
            }
            catch (Exception ex)
            {
                notes.Add("the probe threw: " + ex.GetType().Name + ": " + ex.Message);
                return new MetalTimelineProbeResult { Notes = notes };
            }
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static MetalTimelineProbeResult RunInsidePool(List<string> notes)
        {
            IntPtr device = MTLDevice.CreateSystemDefault();
            if (device == IntPtr.Zero)
            {
                notes.Add("MTLCreateSystemDefaultDevice returned nil, so nothing below could be measured.");
                return new MetalTimelineProbeResult { Notes = notes };
            }

            bool singleton = MeasureDeviceIdentity(device, notes);

            IntPtr queue = ObjCMsgSend.Send(device, ObjCRuntime.Sel("newCommandQueue"));
            if (queue == IntPtr.Zero)
            {
                notes.Add("newCommandQueue returned nil, so no command buffer could carry a signal.");
                ObjCRuntime.ObjcRelease(device);
                return new MetalTimelineProbeResult { DeviceCreated = true, Notes = notes };
            }

            bool twoQueuesRegistered = MeasureTwoQueuesOnOneDevice(device, notes);

            var sink = new RecordingSink();
            MetalCompletionHandler.Register(queue, sink);
            try
            {
                return Measure(device, queue, sink, singleton, twoQueuesRegistered, notes);
            }
            finally
            {
                MetalCompletionHandler.Unregister(queue);
                ObjCRuntime.ObjcRelease(queue);
                ObjCRuntime.ObjcRelease(device);
            }
        }

        /// <summary>
        /// THE CONSEQUENCE OF THE MEASUREMENT ABOVE, ON REAL METAL: two engine devices on ONE GPU each register
        /// their own latch and both registrations are accepted. Keyed on the <c>MTLDevice</c> this could not
        /// pass, because that pointer is the same for both, and the second engine device's creation would fail
        /// with the registered-twice refusal. Two queues on one device is the same shape with the device
        /// creation left out, which is what makes it measurable before row 4 exists.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool MeasureTwoQueuesOnOneDevice(IntPtr device, List<string> notes)
        {
            IntPtr first = ObjCMsgSend.Send(device, ObjCRuntime.Sel("newCommandQueue"));
            IntPtr second = ObjCMsgSend.Send(device, ObjCRuntime.Sel("newCommandQueue"));
            if (first == IntPtr.Zero || second == IntPtr.Zero || first == second)
            {
                notes.Add("two newCommandQueue calls on one device did not hand back two distinct queues, so "
                    + "the routing key's uniqueness is unmeasured on this run.");
                if (first != IntPtr.Zero) ObjCRuntime.ObjcRelease(first);
                if (second != IntPtr.Zero) ObjCRuntime.ObjcRelease(second);
                return false;
            }

            try
            {
                MetalCompletionHandler.Register(first, new RecordingSink());
                MetalCompletionHandler.Register(second, new RecordingSink());
                return true;
            }
            catch (InvalidOperationException ex)
            {
                notes.Add("the second latch registration for the same device was refused: " + ex.Message);
                return false;
            }
            finally
            {
                MetalCompletionHandler.Unregister(first);
                MetalCompletionHandler.Unregister(second);
                ObjCRuntime.ObjcRelease(first);
                ObjCRuntime.ObjcRelease(second);
            }
        }

        /// <summary>
        /// THE MEASUREMENT THAT DECIDES THE ROUTING KEY: is <c>MTLCreateSystemDefaultDevice</c> a per-GPU
        /// PROCESS SINGLETON, so that two engine devices on one GPU present the same <c>MTLDevice</c> pointer?
        /// If it is, keying <see cref="MetalCompletionHandler"/>'s table on the device would refuse the second
        /// engine device's registration outright and would let an unregister/register cycle route a late
        /// completion into the new device's latch, which is the exact failure that table exists to prevent.
        /// <para>
        /// The second reference is released immediately. Whichever way it answers, the value is recorded in the
        /// transcript rather than asserted, because it is a fact about the machine rather than a pass.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool MeasureDeviceIdentity(IntPtr device, List<string> notes)
        {
            IntPtr second = MTLDevice.CreateSystemDefault();
            if (second == IntPtr.Zero)
            {
                notes.Add("the second MTLCreateSystemDefaultDevice returned nil, so the process-singleton "
                    + "question is unmeasured on this run.");
                return false;
            }

            bool same = second == device;
            ObjCRuntime.ObjcRelease(second);
            return same;
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static MetalTimelineProbeResult Measure(
            IntPtr device, IntPtr queue, RecordingSink sink, bool singleton, bool twoQueuesRegistered,
            List<string> notes)
        {
            using var timeline = new MetalTimeline(new MetalSharedEvent(device));
            MetalGpuFence fence = timeline.CreateFence();

            bool unsignaledBeforeArming = fence.Signaled;

            ulong first = Submit(timeline, fence, queue, notes);
            timeline.WaitForIdle();
            ulong signaledAfterFirst = timeline.CompletedValue;
            bool signaledFirst = fence.Signaled;

            fence.Reset();
            bool signaledAfterReset = fence.Signaled;

            ulong second = Submit(timeline, fence, queue, notes);
            timeline.WaitForIdle();
            ulong signaledAfterSecond = timeline.CompletedValue;
            bool signaledSecond = fence.Signaled;

            // The handler is delivered on Metal's own thread with no ordering promise, so this polls with a
            // deadline instead of reading once. Two buffers were committed and both had a handler attached.
            WaitForCompletions(sink, 2, notes);

            (long drains, double drainMs, bool released, bool signaledAfterDeath) =
                MeasureCountedDrain(device, notes);

            return new MetalTimelineProbeResult
            {
                DeviceCreated = true,
                QueueCreated = true,
                SharedEventCreated = true,
                DefaultDeviceIsProcessSingleton = singleton,
                TwoQueuesOnOneDeviceBothRegistered = twoQueuesRegistered,
                FirstValue = first,
                SecondValue = second,
                FenceSignaledBeforeArming = unsignaledBeforeArming,
                SignaledAfterFirstDrain = signaledAfterFirst,
                FenceSignaledAfterFirstDrain = signaledFirst,
                FenceSignaledAfterReset = signaledAfterReset,
                SignaledAfterSecondDrain = signaledAfterSecond,
                FenceSignaledAfterSecondDrain = signaledSecond,
                CompletionsSeen = sink.Count,
                AllCompletionsCompleted = sink.AllCompleted,
                AllCompletionsErrorFree = sink.AllErrorFree,
                FirstCompletionStatus = sink.FirstStatus,
                CountedDrains = drains,
                CountedDrainMs = drainMs,
                DrainReleasedByDeviceDeath = released,
                FenceSignaledAfterDeviceDeath = signaledAfterDeath,
                Notes = notes,
            };
        }

        // One submission, in the order row 7 will make it: take a command buffer, attach the completion handler,
        // encode the signal and arm the fence, then commit, then register. The value is allocated and encoded as
        // one step by the timeline, so there is no window where a value exists without a buffer carrying it.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static ulong Submit(MetalTimeline timeline, MetalGpuFence fence, IntPtr queue, List<string> notes)
        {
            IntPtr commandBuffer = ObjCMsgSend.Send(queue, ObjCRuntime.Sel("commandBuffer"));
            if (!MetalCompletionHandler.AttachTo(commandBuffer))
                notes.Add("addCompletedHandler: was not attached, so no completion was reported for a buffer.");

            ulong value = timeline.EncodeSignalForSubmit(commandBuffer);
            fence.Arm(value);

            ObjCMsgSend.SendVoid(commandBuffer, ObjCRuntime.Sel("commit"));
            timeline.RegisterSubmitted(value);
            return value;
        }

        static void WaitForCompletions(RecordingSink sink, int expected, List<string> notes)
        {
            long deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * DeadlineMs / 1000);
            while (sink.Count < expected && Stopwatch.GetTimestamp() < deadline) Thread.Sleep(1);

            if (sink.Count < expected)
            {
                notes.Add($"only {sink.Count} of {expected} completion handlers had been delivered after "
                    + $"{DeadlineMs}ms.");
            }
        }

        // The counted drain and the slice loop's release, on a SECOND shared event so the first timeline's
        // disposal is untouched by it. See the class note for why this is measured without GPU work.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static (long Drains, double DrainMs, bool Released, bool SignaledAfterDeath) MeasureCountedDrain(
            IntPtr device, List<string> notes)
        {
            var liveness = new MutableLiveness();
            using var timeline = new MetalTimeline(new MetalSharedEvent(device), liveness);
            MetalGpuFence fence = timeline.CreateFence();

            // A value nothing will ever signal, registered as though a commit had accepted it. The real event is
            // waited on for real, times its slice out for real, and the only way out is the flip below.
            timeline.RegisterSubmitted(UnreachableValue);
            fence.Arm(UnreachableValue);

            // THE FLIPPER IS GATED ON THE DRAIN HAVING STARTED, not on a wall-clock sleep. A 20ms sleep is a bet
            // that this thread reaches WaitForIdle within 20ms, which a loaded runner loses: the flip would land
            // BEFORE the drain, the drain would return at its liveness check without ever blocking, and the
            // counted-drain assertions below would be measuring nothing while still reading plausible. The gate
            // is set immediately before the call, so the flip cannot precede the drain's own liveness check by
            // more than the instructions between them.
            using var draining = new ManualResetEventSlim(false);
            var flipper = new Thread(() =>
            {
                draining.Wait();
                liveness.MarkDead();
            })
            { IsBackground = true, Name = "metal-timeline-probe-liveness" };
            flipper.Start();

            draining.Set();
            timeline.WaitForIdle();

            // RELEASED-BY-DEATH IS DERIVED FROM THE DRAIN, NOT FROM THE FLIPPER'S Join. Join measures the
            // flipper thread finishing, which says nothing at all about what the drain did, and it is reached
            // only because WaitForIdle already returned. The drain's own answer is the two facts on this line:
            // execution got past WaitForIdle, so the drain RETURNED, and the timeline never reached the value it
            // was waiting for. The slice loop has exactly two exits, the value arriving and the liveness flip,
            // so a drain that returned without the value can only have been released by the flip.
            bool releasedByDeath = timeline.CompletedValue < UnreachableValue;

            // Hygiene only, and it is separate on purpose: a flipper still running would mean the gate above did
            // not do what it says, which is worth a note even though the drain is already measured.
            if (!flipper.Join(DeadlineMs))
                notes.Add("the liveness flipper thread had not finished after the drain returned.");

            MetalWaitTotals totals = timeline.TotalDrain;
            return (totals.Count, totals.TotalMs, releasedByDeath, fence.Signaled);
        }

        /// <summary>The latch's seat for the duration of the probe: it records rather than decides, because what
        /// a real latch does with a failure is row 4's.</summary>
        sealed class RecordingSink : IMetalCommandBufferErrorSink
        {
            readonly object _gate = new();

            int _count;
            bool _allCompleted = true;
            bool _allErrorFree = true;
            nint _firstStatus = -1;

            internal int Count { get { lock (_gate) return _count; } }
            internal bool AllCompleted { get { lock (_gate) return _allCompleted; } }
            internal bool AllErrorFree { get { lock (_gate) return _allErrorFree; } }
            internal nint FirstStatus { get { lock (_gate) return _firstStatus; } }

            public void CommandBufferCompleted(in MetalCommandBufferOutcome outcome)
            {
                // A lock is fine HERE and would not be fine in the handler: this is a probe's bookkeeping rather
                // than the completion path's contract, and it is the reason the seam hands over a copied-out
                // struct instead of live Objective-C pointers.
                lock (_gate)
                {
                    if (_count == 0) _firstStatus = outcome.Status;
                    _count++;
                    if (outcome.Status != MetalCommandBufferStatus.Completed) _allCompleted = false;
                    if (outcome.Failed || outcome.ErrorCode != 0) _allErrorFree = false;
                }
            }
        }

        /// <summary>A liveness token the probe can flip, standing in for row 4's real one.</summary>
        sealed class MutableLiveness : IDeviceLiveness
        {
            volatile bool _dead;

            public bool IsDead => _dead;

            internal void MarkDead() => _dead = true;
        }
    }
}
