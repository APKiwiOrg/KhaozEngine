using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// An <c>MTLSharedEvent</c> with no device behind it, so everything the native Metal timeline DECIDES (the
    /// monotonic value allocation, the fence target lifecycle, the dead-device answers, and the drain's rule
    /// about what counts as a drain) is driven by a plain <c>[Fact]</c> on a machine with no Metal at all.
    /// <para>
    /// This is the point of <see cref="IMetalSharedEvent"/> being an interface. What is left behind it on the
    /// real path is three Objective-C calls with no ordering logic in them, and everything that could be wrong
    /// about the ORDERING sits above it where a test can reach it.
    /// </para>
    /// <para>
    /// WHAT NO FAKE HERE CAN PROVE is that a value is signalled because real GPU work finished.
    /// <c>MetalTimelineProbe</c> is what proves that, under a <c>[GpuFact]</c> against a real device, and the
    /// boundary between the two is deliberate rather than an omission.
    /// </para>
    /// </summary>
    internal sealed class FakeMetalSharedEvent : IMetalSharedEvent
    {
        /// <summary>What the GPU has reached. Settable, because driving it by hand is how a test pins exactly
        /// which fence reads signalled at which moment.</summary>
        internal ulong Completed { get; set; }

        /// <summary>How many times <see cref="Read"/> has been called. Zero after a dead-device read is the
        /// assertion that the timeline never touched a dead device's event.</summary>
        internal int ReadCount { get; private set; }

        /// <summary>How many times <see cref="WaitUntil"/> has been called. The drain count as the EVENT saw it,
        /// which a test compares against the drain count the timeline reported, and which is more than one when
        /// the drain sliced.</summary>
        internal int WaitCount { get; private set; }

        /// <summary>The value the last wait asked for, or null if nothing has waited.</summary>
        internal ulong? LastWaitValue { get; private set; }

        /// <summary>The timeout the last wait passed, which is what pins the sliced drain to
        /// <see cref="MetalTimeline.DrainSliceMs"/> rather than to some number a caller invented.</summary>
        internal uint? LastWaitTimeoutMs { get; private set; }

        /// <summary>What <see cref="WaitUntil"/> returns. False models a slice that expired without the counter
        /// arriving, which on the real event is a timeout and nothing else.</summary>
        internal bool WaitReachesTheValue { get; set; } = true;

        /// <summary>Every signal encoded, in order, with the buffer it was encoded into. The submit path's whole
        /// observable effect on the event.</summary>
        internal List<(IntPtr CommandBuffer, ulong Value)> Encoded { get; } = new();

        /// <summary>Run at the top of every <see cref="Read"/>, before the value is produced. A test that needs
        /// a device death discovered BY the read (which flips liveness underneath the caller) hangs it
        /// here.</summary>
        internal Action? OnRead { get; set; }

        /// <summary>Run at the top of every <see cref="WaitUntil"/>. A test that needs the counter to advance
        /// while the wait is in progress, or the device to die inside it, hangs it here.</summary>
        internal Action? OnWait { get; set; }

        /// <summary>True once <see cref="Dispose"/> has run, so a test can assert the timeline owns the event and
        /// that a dead device does NOT skip the release, which is where this backend diverges from the Vulkan
        /// one.</summary>
        internal bool Disposed { get; private set; }

        /// <summary>How many times <see cref="Dispose"/> has run. <see cref="Disposed"/> alone cannot tell a
        /// single release from a repeated one, and an over-release of an Objective-C object is a use-after-free
        /// somewhere else entirely.</summary>
        internal int DisposeCount { get; private set; }

        /// <inheritdoc/>
        public ulong Read()
        {
            ReadCount++;
            OnRead?.Invoke();
            return Completed;
        }

        /// <inheritdoc/>
        public bool WaitUntil(ulong value, uint timeoutMs)
        {
            WaitCount++;
            LastWaitValue = value;
            LastWaitTimeoutMs = timeoutMs;
            OnWait?.Invoke();

            if (WaitReachesTheValue && Completed < value) Completed = value;
            return WaitReachesTheValue;
        }

        /// <inheritdoc/>
        public void EncodeSignal(IntPtr commandBuffer, ulong value) => Encoded.Add((commandBuffer, value));

        /// <inheritdoc/>
        public void Dispose()
        {
            Disposed = true;
            DisposeCount++;
        }
    }

    /// <summary>A liveness token a test can flip, standing in for row 4's real one
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/570).</summary>
    internal sealed class FakeMetalDeviceLiveness : IMetalDeviceLiveness
    {
        volatile bool _dead;

        /// <inheritdoc/>
        public bool IsDead => _dead;

        /// <summary>Flip it, permanently, the way both the device's teardown and M-G4's error latch do.</summary>
        internal void MarkDead() => _dead = true;
    }
}
