namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// DECISION V-F10, as a read surface later rows can be built against before the concrete latch is in their
    /// hands: after the device is dead, disposal is a no-op, <c>IGpuFence.Signaled</c> reads TRUE and
    /// <c>WaitForIdle</c> is a no-op. All three say the same thing from different angles, which is that a
    /// destroyed device has no outstanding GPU work left to finish, so the honest answer to "is it done" is yes
    /// and the honest answer to "wait for it" is to return.
    /// <para>
    /// GETTING IT WRONG IS NOT COSMETIC. A fence that read UNSIGNALLED after device death would strand
    /// <c>RetiredResourcePool</c> forever on a batch it can never free, and a drain that spun would spin on a
    /// counter nothing can advance any more. Both are teardown-order hazards, and teardown order is exactly where
    /// a resource wrapper outliving its device is normal rather than a defect.
    /// </para>
    /// <para>
    /// AN INTERFACE RATHER THAN THE DEVICE ITSELF, for the reason the Direct3D 11 package's equivalent gives:
    /// reaching the device would make every wrapper hold the device, which is the reference cycle the incumbent
    /// deliberately does not have, and it keeps the whole behaviour headless-testable.
    /// <see cref="VulkanTimeline"/> is the first consumer, and all three behaviours above are asserted through it
    /// on a machine with no Vulkan loader.
    /// </para>
    /// </summary>
    internal interface IVulkanDeviceLiveness
    {
        /// <summary>True once the device has been destroyed. Read lock-free on every fence poll and at the top of
        /// every drain, so an implementation must be cheap and must never take a lock.</summary>
        bool IsDead { get; }
    }

    /// <summary>
    /// The shared "is the device still there" token, decision V-F10, reproduced exactly from the incumbent's
    /// <c>DeviceLiveness</c> and from the Direct3D 11 package's <c>D3D11DeviceLiveness</c>. One instance per
    /// device, handed to every wrapper the device creates, flipped ONCE BEFORE the real device is destroyed, and
    /// read lock-free afterwards.
    /// <para>
    /// TWO THINGS FLIP IT, for the same underlying reason. The device's own teardown flips it inside its
    /// lifecycle lock, after <c>vkDeviceWaitIdle</c> and before <c>vkDestroyDevice</c>, which is the ordinary
    /// case. <see cref="VulkanDeviceLossLatch"/> flips it on a DEVICE LOSS, which is the other way a device stops
    /// existing: the objects are already gone, nothing asked for it, and every destroy call from that point on
    /// would be a call against freed memory.
    /// </para>
    /// <para>
    /// WHAT IT BUYS, and this backend is where the evidence came from. Destroying a <c>VkDevice</c> already
    /// destroys every child object made from it, so a wrapper disposed after its device is destroying something
    /// that no longer exists. On the Veldrid Vulkan path that call ABORTED THE PROCESS through the loader rather
    /// than failing quietly, and the two teardown-order crashes it produced are why the token exists at all.
    /// Every wrapper's <c>Dispose</c> is therefore gated on <see cref="IsAlive"/>.
    /// </para>
    /// <para>
    /// THE MEMORY MODEL IS THE WHOLE IMPLEMENTATION. The field is <c>volatile</c>, so a reader on another thread
    /// sees the flip without a lock, which is the point: disposal runs on whatever thread the consumer happens to
    /// be on, and taking the device's lifecycle lock there would deadlock against the very teardown that flipped
    /// this. There is deliberately no way to flip it back. A device that has been destroyed does not come back,
    /// and an un-kill would turn a stale wrapper into a call against freed memory.
    /// </para>
    /// </summary>
    internal sealed class VulkanDeviceLiveness : IVulkanDeviceLiveness
    {
        volatile bool _dead;

        /// <summary>True while the real device is still alive and a native destroy is safe to make.</summary>
        internal bool IsAlive => !_dead;

        /// <summary>True once <see cref="MarkDead"/> has run. Every native destroy must be skipped from here on,
        /// a fence reads as signaled, and a drain does nothing. Public because it implements
        /// <see cref="IVulkanDeviceLiveness"/>, which is no wider than the class: the class is internal.</summary>
        public bool IsDead => _dead;

        /// <summary>
        /// Flip the token, permanently. Called by the device's own teardown, inside its lifecycle lock, AFTER
        /// <c>vkDeviceWaitIdle</c> and BEFORE <c>vkDestroyDevice</c>, and by
        /// <see cref="VulkanDeviceLossLatch"/> the moment a device loss is latched, which is the same statement
        /// arriving from the driver instead of from the application. Idempotent, and safe to call from any
        /// thread.
        /// </summary>
        internal void MarkDead() => _dead = true;
    }

    /// <summary>The default when no liveness token has been wired in yet: the device is alive and stays alive.
    /// <para>This is the SAFE default rather than a convenient one. Defaulting to dead would make every fence
    /// read signalled and every drain a no-op, which is the failure V-F10 exists to produce only AFTER death and
    /// is silent before it: a pool would free resources the GPU is still reading and the corruption would surface
    /// somewhere else entirely.</para></summary>
    internal sealed class VulkanLiveDevice : IVulkanDeviceLiveness
    {
        /// <summary>The shared instance. Stateless, so one is enough for the process.</summary>
        internal static readonly VulkanLiveDevice Instance = new();

        VulkanLiveDevice() { }

        /// <inheritdoc/>
        public bool IsDead => false;
    }
}
