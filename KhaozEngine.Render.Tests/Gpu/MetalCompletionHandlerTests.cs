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
    /// THE REGISTRY IS PROCESS-STATIC, which is why this class shares a serialised collection with the GPU test
    /// that registers a real device against it. Two suites filling the same four-slot table concurrently would
    /// fail each other for a reason that is a test-harness artefact rather than a defect.
    /// </para>
    /// </summary>
    [Collection("MetalCompletionRegistry")]
    public sealed class MetalCompletionHandlerTests
    {
        // Opaque stand-ins for MTLDevice handles. Nothing dereferences them: the table is keyed on the pointer
        // and compares it, which is the whole of the lookup on the completion path.
        static IntPtr Device(int n) => new(0x3000 + n);

        static MetalCommandBufferOutcome Completed()
            => new(MetalCommandBufferStatus.Completed, 0, "");

        static MetalCommandBufferOutcome Failed()
            => new(MetalCommandBufferStatus.Error, 5, "Caused GPU Timeout Error (00000002:kIOAccelCommandBufferCallbackErrorTimeout)");

        [Fact]
        public void ACompletion_ReachesTheSinkRegisteredForThatDevice()
        {
            var sink = new RecordingSink();
            MetalCompletionHandler.Register(Device(1), sink);
            try
            {
                MetalCompletionHandler.Deliver(Device(1), Completed());

                MetalCommandBufferOutcome only = Assert.Single(sink.Seen);
                Assert.Equal(MetalCommandBufferStatus.Completed, only.Status);
                Assert.False(only.Failed);
            }
            finally
            {
                MetalCompletionHandler.Unregister(Device(1));
            }
        }

        [Fact]
        public void ACompletion_ReachesOnlyItsOwnDevicesSink()
        {
            var first = new RecordingSink();
            var second = new RecordingSink();
            MetalCompletionHandler.Register(Device(2), first);
            MetalCompletionHandler.Register(Device(3), second);
            try
            {
                MetalCompletionHandler.Deliver(Device(3), Failed());

                // Delivering device A's failure to device B's latch would flip the wrong liveness token, which
                // is the whole reason the block reads the device off the command buffer rather than carrying no
                // key at all.
                Assert.Empty(first.Seen);
                MetalCommandBufferOutcome only = Assert.Single(second.Seen);
                Assert.True(only.Failed);
                Assert.Equal(5, only.ErrorCode);
                Assert.Contains("Timeout", only.ErrorDescription, StringComparison.Ordinal);
            }
            finally
            {
                MetalCompletionHandler.Unregister(Device(2));
                MetalCompletionHandler.Unregister(Device(3));
            }
        }

        [Fact]
        public void ACompletionForAnUnregisteredDevice_ReachesNobodyAndIsQuiet()
        {
            var sink = new RecordingSink();
            MetalCompletionHandler.Register(Device(4), sink);
            try
            {
                MetalCompletionHandler.Deliver(Device(5), Completed());
                Assert.Empty(sink.Seen);
            }
            finally
            {
                MetalCompletionHandler.Unregister(Device(4));
            }
        }

        [Fact]
        public void Unregister_StopsDelivery()
        {
            var sink = new RecordingSink();
            MetalCompletionHandler.Register(Device(6), sink);
            MetalCompletionHandler.Unregister(Device(6));

            // A buffer completing after its device has been torn down has nothing left to latch on to, which is
            // why the device slot is cleared before the sink slot.
            MetalCompletionHandler.Deliver(Device(6), Completed());
            Assert.Empty(sink.Seen);
        }

        [Fact]
        public void Unregister_ForADeviceThatNeverRegistered_IsQuiet()
            => MetalCompletionHandler.Unregister(Device(7));

        [Fact]
        public void RegisteringTheSameDeviceTwice_IsRefused()
        {
            MetalCompletionHandler.Register(Device(8), new RecordingSink());
            try
            {
                // A latch that was replaced would stop hearing about the failures of buffers already in flight
                // against it, so the second registration is refused rather than winning.
                Assert.Throws<InvalidOperationException>(
                    () => MetalCompletionHandler.Register(Device(8), new RecordingSink()));
            }
            finally
            {
                MetalCompletionHandler.Unregister(Device(8));
            }
        }

        [Fact]
        public void RegisteringMoreDevicesThanTheTableHolds_IsRefused()
        {
            var registered = new List<IntPtr>();
            try
            {
                // The scan runs per command buffer on the completion path, so the table is deliberately small.
                // This walks up to one past capacity rather than asserting a fixed count, because another suite
                // in this collection's process may legitimately hold a slot.
                Exception? refused = null;
                for (int i = 0; i <= MetalCompletionHandler.MaxRegisteredDevices; i++)
                {
                    IntPtr device = Device(100 + i);
                    try
                    {
                        MetalCompletionHandler.Register(device, new RecordingSink());
                        registered.Add(device);
                    }
                    catch (InvalidOperationException ex)
                    {
                        refused = ex;
                        break;
                    }
                }

                Assert.NotNull(refused);
                Assert.True(registered.Count <= MetalCompletionHandler.MaxRegisteredDevices);
            }
            finally
            {
                foreach (IntPtr device in registered) MetalCompletionHandler.Unregister(device);
            }
        }

        [Fact]
        public void ASinkThatThrows_DoesNotEscapeTheCompletionPath()
        {
            MetalCompletionHandler.Register(Device(9), new ThrowingSink());
            try
            {
                // The real caller is an Objective-C callback, where an escaping exception terminates the process
                // rather than unwinding to anything that could report it.
                MetalCompletionHandler.Deliver(Device(9), Failed());
            }
            finally
            {
                MetalCompletionHandler.Unregister(Device(9));
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
        public void RegisteringWithNoSinkOrNoDevice_IsRefused()
        {
            Assert.Throws<ArgumentNullException>(
                () => MetalCompletionHandler.Register(IntPtr.Zero, new RecordingSink()));
            Assert.Throws<ArgumentNullException>(() => MetalCompletionHandler.Register(Device(10), null!));
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

    /// <summary>
    /// The completion registry is one process-static table of four slots, so every suite that registers into it
    /// runs serially against every other. That is the same reason <c>NativeDeviceLifecycle</c> exists, applied
    /// to a smaller piece of shared state: a suite filling the table while another is registering would fail it
    /// for a reason that is a harness artefact rather than a defect.
    /// </summary>
    [CollectionDefinition("MetalCompletionRegistry", DisableParallelization = true)]
    public sealed class MetalCompletionRegistryCollection { }
}
