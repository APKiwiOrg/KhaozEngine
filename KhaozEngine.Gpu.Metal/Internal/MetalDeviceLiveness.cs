namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// DECISION M-F6, as a read surface later rows can be built against before the concrete latch is in their
    /// hands: after the device is dead, disposal is a no-op, <c>IGpuFence.Signaled</c> reads TRUE and
    /// <c>WaitForIdle</c> is a no-op. All three say the same thing from different angles, which is that a
    /// destroyed device has no outstanding GPU work left to finish, so the honest answer to "is it done" is yes
    /// and the honest answer to "wait for it" is to return.
    /// <para>
    /// GETTING IT WRONG IS NOT COSMETIC. A fence that read UNSIGNALLED after device death would strand a retire
    /// pool forever on a batch it can never free, and a drain that spun would spin on a counter nothing can
    /// advance any more. Both are teardown-order hazards, and teardown order is exactly where a resource wrapper
    /// outliving its device is normal rather than a defect.
    /// </para>
    /// <para>
    /// AN INTERFACE RATHER THAN THE DEVICE ITSELF, for the reason both sibling backends give: reaching the device
    /// would make every wrapper hold the device, which is the reference cycle the incumbent deliberately does not
    /// have, and it keeps the whole behaviour headless-testable. The timeline row
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/571) is the first consumer that will read it on a hot
    /// path.
    /// </para>
    /// <para>
    /// THIS IS X3 AND V-F10 REPRODUCED EXACTLY, and M-F6 says so in as many words. It is about the ENGINE's
    /// teardown order rather than about Objective-C reference counting, which is why the absence of a retire list
    /// in this backend (M-H3) does not touch it: an over-release of a Metal object and a wrapper disposed after
    /// its device are different failures with different fixes, and this one is the second.
    /// </para>
    /// </summary>
    internal interface IMetalDeviceLiveness
    {
        /// <summary>True once the device has been torn down or lost. Read lock-free on every fence poll and at
        /// the top of every drain, so an implementation must be cheap and must never take a lock.</summary>
        bool IsDead { get; }
    }

    /// <summary>
    /// The shared "is the device still there" token. One instance per device, handed to every wrapper the device
    /// creates, flipped ONCE inside the lifecycle lock before the real device is released, and read lock-free
    /// afterwards.
    /// <para>
    /// TWO THINGS FLIP IT, for the same underlying reason. The device's own teardown flips it inside its
    /// lifecycle lock, AFTER the drain and BEFORE the queue and the device are released, which is the ordinary
    /// case and is M-F6's order. <see cref="MetalDeviceLossLatch"/> flips it on a command-buffer failure, which
    /// is the other way a device stops being usable: the driver has already given up on the work, and on the
    /// codes that mean the device itself went there is nothing left to release safely.
    /// </para>
    /// <para>
    /// THE MEMORY MODEL IS THE WHOLE IMPLEMENTATION. The field is <c>volatile</c>, so a reader on another thread
    /// sees the flip without a lock, which is the point: disposal runs on whatever thread the consumer happens to
    /// be on, and taking the device's lifecycle lock there would deadlock against the very teardown that flipped
    /// this. Metal's completion handlers arrive on an arbitrary internal thread, so the latch's flip is a
    /// cross-thread write by construction rather than by accident. There is deliberately no way to flip it back:
    /// a device that has been torn down does not come back, and an un-kill would turn a stale wrapper into a call
    /// against a released object.
    /// </para>
    /// </summary>
    internal sealed class MetalDeviceLiveness : IMetalDeviceLiveness
    {
        volatile bool _dead;

        /// <summary>True while the real device is still alive and a native release is safe to make.</summary>
        internal bool IsAlive => !_dead;

        /// <summary>True once <see cref="MarkDead"/> has run. Every native release must be skipped from here on,
        /// a fence reads as signaled, and a drain does nothing. Public because it implements
        /// <see cref="IMetalDeviceLiveness"/>, which is no wider than the class: the class is internal.</summary>
        public bool IsDead => _dead;

        /// <summary>
        /// Flip the token, permanently. Called by the device's own teardown, inside its lifecycle lock, AFTER the
        /// drain and BEFORE the queue and device are released, and by <see cref="MetalDeviceLossLatch"/> the
        /// moment a command-buffer failure is latched, which is the same statement arriving from the driver
        /// instead of from the application. Idempotent, and safe to call from any thread.
        /// </summary>
        internal void MarkDead() => _dead = true;
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
