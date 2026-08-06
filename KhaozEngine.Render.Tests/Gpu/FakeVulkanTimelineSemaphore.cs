using System;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A timeline semaphore with no device behind it, so everything the native Vulkan timeline DECIDES (the
    /// monotonic value allocation, the fence target lifecycle, the dead-device answers, the drain's rule about
    /// what counts as a drain, and the retire list's release rule) is driven by a plain <c>[Fact]</c> on a machine
    /// with no Vulkan loader.
    /// <para>
    /// This is the point of <see cref="IVulkanTimelineSemaphore"/> being an interface at all. What is left behind
    /// it on the real path is three driver calls with no ordering logic in them, and everything that could be
    /// wrong about the ORDERING sits above it where a test can reach it.
    /// </para>
    /// <para>
    /// WHAT NO FAKE HERE CAN PROVE is that a value is signalled because real GPU work finished. Nothing submits on
    /// this backend yet: row 7 (https://github.com/APKiwiOrg/KhaozEngine/issues/517) owns the submit path, and
    /// that is the row where a counter first advances because a queue drained rather than because a test set a
    /// property. The boundary is deliberate, not an omission.
    /// </para>
    /// </summary>
    internal sealed class FakeVulkanTimelineSemaphore : IVulkanTimelineSemaphore
    {
        /// <summary>What the GPU has reached. Settable, because driving it by hand is how a test pins exactly
        /// which fence reads signalled at which moment.</summary>
        internal ulong Completed { get; set; }

        /// <summary>How many times <see cref="Read"/> has been called. Zero after a dead-device read is the
        /// assertion that the timeline never touched a destroyed device's semaphore.</summary>
        internal int ReadCount { get; private set; }

        /// <summary>How many times <see cref="WaitUntil"/> has been called. The drain count as the SEMAPHORE saw
        /// it, which a test compares against the drain count the timeline reported.</summary>
        internal int WaitCount { get; private set; }

        /// <summary>The value the last wait asked for, or null if nothing has waited.</summary>
        internal ulong? LastWaitValue { get; private set; }

        /// <summary>What <see cref="WaitUntil"/> returns. False models a wait that ended because the device was
        /// LOST, which the real semaphore latches at its own site before returning.</summary>
        internal bool WaitReachesTheValue { get; set; } = true;

        /// <summary>Run at the top of every <see cref="Read"/>, before the value is produced. A test that needs a
        /// device loss discovered BY the read (which flips liveness underneath the caller) hangs it here.</summary>
        internal Action? OnRead { get; set; }

        /// <summary>Run at the top of every <see cref="WaitUntil"/>. A test that needs the counter to advance
        /// while the wait is in progress, or the device to die inside it, hangs it here.</summary>
        internal Action? OnWait { get; set; }

        /// <summary>True once <see cref="Dispose"/> has run, so a test can assert the timeline owns the semaphore
        /// and that a dead device skips the native destroy.</summary>
        internal bool Disposed { get; private set; }

        /// <summary>How many times <see cref="Dispose"/> has run. <see cref="Disposed"/> alone cannot tell a
        /// single destroy from a repeated one, and the timeline's own guard against destroying the semaphore
        /// twice is exactly what this counts.</summary>
        internal int DisposeCount { get; private set; }

        /// <inheritdoc/>
        public ulong Read()
        {
            ReadCount++;
            OnRead?.Invoke();
            return Completed;
        }

        /// <inheritdoc/>
        public bool WaitUntil(ulong value)
        {
            WaitCount++;
            LastWaitValue = value;
            OnWait?.Invoke();

            if (WaitReachesTheValue && Completed < value) Completed = value;
            return WaitReachesTheValue;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Disposed = true;
            DisposeCount++;
        }
    }
}
