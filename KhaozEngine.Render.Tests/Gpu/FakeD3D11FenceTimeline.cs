using System;
using KhaozEngine.Gpu.D3D11.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A completion timeline with no device behind it, so the engine half of the native Direct3D 11 fence
    /// subsystem (the target lifecycle, the drain loop, the kill switch, the liveness behaviour and the
    /// per-frame counters) is driven by a plain <c>[Fact]</c> on macOS and Linux as well as Windows.
    /// <para>
    /// This is the point of <see cref="ID3D11FenceTimeline"/> being an interface at all. What is left behind it
    /// on the real paths is two native calls per mechanism, and everything that could be wrong about the
    /// ORDERING sits above it where a test can reach it.
    /// </para>
    /// <para>
    /// COMPLETION IS MANUAL BY DEFAULT: a test either sets <see cref="Completed"/> itself or sets
    /// <see cref="AutoCompleteAfterPolls"/> so a drain finishes after a known number of polls. A test that drains
    /// against neither would spin forever, so the fake counts its polls and throws instead, turning a hung suite
    /// into a named failure.
    /// </para>
    /// </summary>
    internal sealed class FakeD3D11FenceTimeline : ID3D11FenceTimeline
    {
        // Generous enough that no legitimate test reaches it, small enough that hitting it is instant.
        internal const int RunawayPollLimit = 10_000;

        /// <inheritdoc/>
        public D3D11FenceMechanism Mechanism { get; set; } = D3D11FenceMechanism.MonotonicFence;

        /// <summary>The last value handed out by <see cref="Signal"/>.</summary>
        internal ulong Issued { get; private set; }

        /// <summary>What the GPU has reached. Settable, because driving it by hand is how a test pins exactly
        /// which fence reads signalled at which moment.</summary>
        internal ulong Completed { get; set; }

        /// <summary>How many times <see cref="CompletedValue"/> has been read. The drain loop's poll count.
        /// </summary>
        internal int PollCount { get; private set; }

        /// <summary>How many times <see cref="Signal"/> has been called. Equal to <see cref="Issued"/>, named
        /// separately because a test asserting "the replay tail signalled once" is asserting about calls.</summary>
        internal int SignalCount { get; private set; }

        /// <summary>True once <see cref="Dispose"/> has run, so a test can assert the subsystem owns the
        /// timeline.</summary>
        internal bool Disposed { get; private set; }

        /// <summary>When set, the timeline completes everything issued once it has been polled this many times.
        /// Null (the default) leaves completion entirely to the test.</summary>
        internal int? AutoCompleteAfterPolls { get; set; }

        /// <inheritdoc/>
        public ulong Signal()
        {
            SignalCount++;
            Issued++;
            return Issued;
        }

        /// <inheritdoc/>
        public ulong CompletedValue
        {
            get
            {
                PollCount++;
                if (AutoCompleteAfterPolls is int after && PollCount >= after) Completed = Issued;

                if (AutoCompleteAfterPolls is null && PollCount > RunawayPollLimit)
                {
                    throw new InvalidOperationException(
                        $"A fake Direct3D 11 fence timeline was polled {PollCount} times without completing. The "
                        + "test is draining against a timeline whose completion nobody drives, so set "
                        + "AutoCompleteAfterPolls or advance Completed by hand. Failing here rather than "
                        + "spinning, because the alternative is a suite that hangs with no name on it.");
                }

                return Completed;
            }
        }

        /// <inheritdoc/>
        public void Dispose() => Disposed = true;
    }

    /// <summary>The device liveness latch a test flips by hand, standing in for the one the native device's
    /// resources row builds. See <see cref="ID3D11DeviceLiveness"/> for what decision X3 requires of it.</summary>
    internal sealed class FakeD3D11DeviceLiveness : ID3D11DeviceLiveness
    {
        /// <inheritdoc/>
        public bool IsDead { get; set; }
    }
}
