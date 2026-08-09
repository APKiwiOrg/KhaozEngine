namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// DECISION M-F6, as a read surface the timeline is built against before the concrete latch is in its hands:
    /// after the device is dead, <c>IGpuFence.Signaled</c> reads TRUE and <c>WaitForIdle</c> returns. Both say
    /// the same thing from different angles, which is that a device with nothing left to finish answers yes to
    /// "is it done" and returns to "wait for it".
    /// <para>
    /// GETTING IT WRONG IS NOT COSMETIC. A fence that read UNSIGNALLED after device death would strand
    /// <c>RetiredResourcePool</c> forever on a batch it can never free, and a drain would block on a value
    /// nothing can advance any more. Both are teardown-order hazards, and teardown order is exactly where a
    /// resource wrapper outliving its device is normal rather than a defect.
    /// </para>
    /// <para>
    /// THE CONCRETE LATCH IS ROW 4'S (https://github.com/APKiwiOrg/KhaozEngine/issues/570), which owns the
    /// device, its teardown order and M-G4's error latch. This interface exists here because row 5 is pulled
    /// ahead of nothing it can wait for: row 8's ring reads a completion value, so the timeline lands first and
    /// needs a liveness answer on day one. Row 4 implements this interface with its <c>DeviceLiveness</c> token
    /// rather than declaring a second one, which is recorded on #570 as part of this row's handoff.
    /// </para>
    /// <para>
    /// TWO THINGS WILL FLIP IT, for the same underlying reason. The device's own teardown flips it inside the
    /// lifecycle lock, after the timeline drain and before the queue and device are released, which is the
    /// ordinary case (M-F6). The completion handler's error latch flips it the moment a command buffer comes
    /// back with <c>MTLCommandBufferStatus.Error</c> (M-G4), which is the same statement arriving from the
    /// driver instead of from the application.
    /// </para>
    /// <para>
    /// AN INTERFACE RATHER THAN THE DEVICE ITSELF, for the reason both sibling backends give: reaching the
    /// device would make every wrapper hold the device, which is the reference cycle the incumbent deliberately
    /// does not have, and it keeps the whole behaviour headless-testable. <see cref="MetalTimeline"/> is the
    /// first consumer and every one of these behaviours is asserted through it on a machine with no Metal.
    /// </para>
    /// </summary>
    internal interface IMetalDeviceLiveness
    {
        /// <summary>True once the device is gone, whether it was torn down or lost. Read lock-free on every
        /// fence poll and at the top of every drain, so an implementation must be cheap and must never take a
        /// lock.</summary>
        bool IsDead { get; }
    }

    /// <summary>The default when no liveness token has been wired in yet: the device is alive and stays alive.
    /// <para>This is the SAFE default rather than a convenient one. Defaulting to dead would make every fence
    /// read signalled and every drain a no-op, which is the behaviour M-F6 wants only AFTER death and which is
    /// silent before it: a pool would free resources the GPU is still reading and the corruption would surface
    /// somewhere else entirely.</para></summary>
    internal sealed class MetalLiveDevice : IMetalDeviceLiveness
    {
        /// <summary>The shared instance. Stateless, so one is enough for the process.</summary>
        internal static readonly MetalLiveDevice Instance = new();

        MetalLiveDevice() { }

        /// <inheritdoc/>
        public bool IsDead => false;
    }
}
