using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The completion handler's DECIDING half, device-free (M-F2, M-G4). Row 5 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    /// <para>
    /// The handler itself is an <c>[UnmanagedCallersOnly]</c> invoke that reads <c>status</c> and <c>error</c>
    /// off a real command buffer, which needs a device and is <c>MetalTimelineGpuTests</c>'s. What is device-free
    /// is the routing: which sink a completed buffer's outcome reaches, what happens when it reaches none, and
    /// what happens when the sink throws inside a callback that must not let anything escape.
    /// </para>
    /// <para>
    /// THE REGISTRY IS PROCESS-STATIC, which is why this class sits in <c>NativeDeviceLifecycle</c> alongside
    /// the GPU test that registers a real queue into the same four-slot table. Two suites filling it
    /// concurrently would fail each other for a reason that is a test-harness artefact rather than a defect, and
    /// one collection is what makes the two run in sequence: xUnit runs the CLASSES of a collection one at a
    /// time, where two separate non-parallel collections would only be ordered by the runner's own rules. See
    /// <see cref="NativeDeviceLifecycleCollection"/> for the collection's other, older reason to exist.
    /// </para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class MetalCompletionHandlerTests
    {
        // Opaque stand-ins for MTLCommandQueue handles. Nothing dereferences them: the table is keyed on the
        // pointer and compares it, which is the whole of the lookup on the completion path.
        static IntPtr Queue(int n) => new(0x3000 + n);

        static MetalCommandBufferOutcome Completed()
            => new(MetalCommandBufferStatus.Completed, 0, "");

        static MetalCommandBufferOutcome Failed()
            => new(MetalCommandBufferStatus.Error, 5, "Caused GPU Timeout Error (00000002:kIOAccelCommandBufferCallbackErrorTimeout)");

        [Fact]
        public void ACompletion_ReachesTheSinkRegisteredForThatQueue()
        {
            var sink = new RecordingSink();
            MetalCompletionHandler.Register(Queue(1), sink);
            try
            {
                MetalCompletionHandler.Deliver(Queue(1), Completed());

                MetalCommandBufferOutcome only = Assert.Single(sink.Seen);
                Assert.Equal(MetalCommandBufferStatus.Completed, only.Status);
                Assert.False(only.Failed);
            }
            finally
            {
                MetalCompletionHandler.Unregister(Queue(1));
            }
        }

        [Fact]
        public void ACompletion_ReachesOnlyItsOwnQueuesSink()
        {
            var first = new RecordingSink();
            var second = new RecordingSink();
            MetalCompletionHandler.Register(Queue(2), first);
            MetalCompletionHandler.Register(Queue(3), second);
            try
            {
                MetalCompletionHandler.Deliver(Queue(3), Failed());

                // Delivering device A's failure to device B's latch would flip the wrong liveness token, which
                // is the whole reason the block reads the queue off the command buffer rather than carrying no
                // key at all.
                Assert.Empty(first.Seen);
                MetalCommandBufferOutcome only = Assert.Single(second.Seen);
                Assert.True(only.Failed);
                Assert.Equal(5, only.ErrorCode);
                Assert.Contains("Timeout", only.ErrorDescription, StringComparison.Ordinal);
            }
            finally
            {
                MetalCompletionHandler.Unregister(Queue(2));
                MetalCompletionHandler.Unregister(Queue(3));
            }
        }

        [Fact]
        public void ACompletionForAnUnregisteredQueue_ReachesNobodyAndIsQuiet()
        {
            var sink = new RecordingSink();
            MetalCompletionHandler.Register(Queue(4), sink);
            try
            {
                MetalCompletionHandler.Deliver(Queue(5), Completed());
                Assert.Empty(sink.Seen);
            }
            finally
            {
                MetalCompletionHandler.Unregister(Queue(4));
            }
        }

        [Fact]
        public void Unregister_StopsDelivery()
        {
            var sink = new RecordingSink();
            MetalCompletionHandler.Register(Queue(6), sink);
            MetalCompletionHandler.Unregister(Queue(6));

            // A buffer completing after its device has been torn down has nothing left to latch on to, which is
            // why the queue slot is cleared before the sink slot.
            MetalCompletionHandler.Deliver(Queue(6), Completed());
            Assert.Empty(sink.Seen);
        }

        [Fact]
        public void ALateCompletionFromATornDownDevice_DoesNotReachItsSuccessorsLatch()
        {
            // THE FAILURE THE QUEUE KEY EXISTS FOR. MTLCreateSystemDefaultDevice is a per-GPU process singleton
            // (measured, see MetalTimelineProbe), so an engine device torn down and replaced presents the SAME
            // MTLDevice pointer to the table. Keyed on the device, this delivery would land in the successor's
            // latch and flip the liveness token of a device that is perfectly healthy. Keyed on the queue, the
            // old buffer's key is a queue nobody holds any more.
            var torn = new RecordingSink();
            var successor = new RecordingSink();

            MetalCompletionHandler.Register(Queue(11), torn);
            MetalCompletionHandler.Unregister(Queue(11));
            MetalCompletionHandler.Register(Queue(12), successor);
            try
            {
                MetalCompletionHandler.Deliver(Queue(11), Failed());

                Assert.Empty(successor.Seen);
                Assert.Empty(torn.Seen);
            }
            finally
            {
                MetalCompletionHandler.Unregister(Queue(12));
            }
        }

        [Fact]
        public void TwoQueuesOnOneDevice_EachRegisterTheirOwnLatch()
        {
            // The same measurement from the other side: two engine devices on one GPU are indistinguishable by
            // MTLDevice pointer, so a device-keyed table would refuse the second one's registration outright and
            // its creation would fail. Two distinct queues are two distinct keys.
            var first = new RecordingSink();
            var second = new RecordingSink();

            MetalCompletionHandler.Register(Queue(13), first);
            MetalCompletionHandler.Register(Queue(14), second);
            try
            {
                MetalCompletionHandler.Deliver(Queue(14), Completed());

                Assert.Empty(first.Seen);
                Assert.Single(second.Seen);
            }
            finally
            {
                MetalCompletionHandler.Unregister(Queue(13));
                MetalCompletionHandler.Unregister(Queue(14));
            }
        }

        [Fact]
        public void Unregister_ForAQueueThatNeverRegistered_IsQuiet()
            => MetalCompletionHandler.Unregister(Queue(7));

        [Fact]
        public void RegisteringTheSameQueueTwice_IsRefused()
        {
            MetalCompletionHandler.Register(Queue(8), new RecordingSink());
            try
            {
                // A latch that was replaced would stop hearing about the failures of buffers already in flight
                // against it, so the second registration is refused rather than winning.
                Assert.Throws<InvalidOperationException>(
                    () => MetalCompletionHandler.Register(Queue(8), new RecordingSink()));
            }
            finally
            {
                MetalCompletionHandler.Unregister(Queue(8));
            }
        }

        [Fact]
        public void RegisteringMoreQueuesThanTheTableHolds_IsRefused()
        {
            var registered = new List<IntPtr>();
            try
            {
                // The scan runs per command buffer on the completion path, so the table is deliberately small.
                // The count is EXACT rather than tolerant: this class and the GPU probe are the only registrants
                // in the assembly and they share one collection, whose classes xUnit runs one at a time, so the
                // table is empty when this starts. A tolerant "at most capacity" would pass with zero slots
                // free, which is the assertion emptying itself.
                Exception? refused = null;
                for (int i = 0; i <= MetalCompletionHandler.MaxRegisteredQueues; i++)
                {
                    IntPtr queue = Queue(100 + i);
                    try
                    {
                        MetalCompletionHandler.Register(queue, new RecordingSink());
                        registered.Add(queue);
                    }
                    catch (InvalidOperationException ex)
                    {
                        refused = ex;
                        break;
                    }
                }

                Assert.NotNull(refused);
                Assert.Equal(MetalCompletionHandler.MaxRegisteredQueues, registered.Count);
            }
            finally
            {
                foreach (IntPtr queue in registered) MetalCompletionHandler.Unregister(queue);
            }
        }

        [Fact]
        public void ASinkThatThrows_DoesNotEscapeTheCompletionPath()
        {
            MetalCompletionHandler.Register(Queue(9), new ThrowingSink());
            try
            {
                // The real caller is an Objective-C callback, where an escaping exception terminates the process
                // rather than unwinding to anything that could report it.
                MetalCompletionHandler.Deliver(Queue(9), Failed());
            }
            finally
            {
                MetalCompletionHandler.Unregister(Queue(9));
            }
        }

        [Fact]
        public void AnOutcome_ReportsFailedOnlyForTheErrorStatus()
        {
            Assert.False(Completed().Failed);
            Assert.True(Failed().Failed);
            Assert.Equal(4, MetalCommandBufferStatus.Completed);
            Assert.Equal(5, MetalCommandBufferStatus.Error);
        }

        [Fact]
        public void RegisteringWithNoSinkOrNoQueue_IsRefused()
        {
            Assert.Throws<ArgumentNullException>(
                () => MetalCompletionHandler.Register(IntPtr.Zero, new RecordingSink()));
            Assert.Throws<ArgumentNullException>(() => MetalCompletionHandler.Register(Queue(10), null!));
        }

        sealed class RecordingSink : IMetalCommandBufferErrorSink
        {
            internal List<MetalCommandBufferOutcome> Seen { get; } = new();

            public void CommandBufferCompleted(in MetalCommandBufferOutcome outcome) => Seen.Add(outcome);
        }

        sealed class ThrowingSink : IMetalCommandBufferErrorSink
        {
            public void CommandBufferCompleted(in MetalCommandBufferOutcome outcome)
                => throw new InvalidOperationException("a latch that throws inside a driver callback");
        }
    }
}
