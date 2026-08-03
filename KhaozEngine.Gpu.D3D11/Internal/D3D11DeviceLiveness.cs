namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// The shared "is the device still there" token, decision X3, reproduced from the incumbent's
    /// <c>DeviceLiveness</c>. One instance per device, handed to every wrapper the device creates, flipped ONCE by
    /// the context inside its lifecycle lock BEFORE the real device is destroyed, and read lock-free afterwards.
    /// <para>
    /// WHAT IT BUYS. Destroying a Direct3D device already frees every child object it made, so a wrapper disposed
    /// after its device would be releasing something that no longer exists. On the Veldrid path the equivalent
    /// call aborted the process through the Vulkan loader rather than failing quietly, and the two teardown-order
    /// crashes that produced are why the token exists at all. Every wrapper's <c>Dispose</c> is therefore gated on
    /// <see cref="IsAlive"/>, which makes post-death disposal a no-op instead of a fault, and the gate is what lets
    /// a consumer keep the ordinary "dispose your resources whenever" contract.
    /// </para>
    /// <para>
    /// WHY IT IS ITS OWN TYPE rather than a bool on the device. Two things outside resource disposal read it. The
    /// fence work reads it so <c>IGpuFence.Signaled</c> answers TRUE once the device is dead (a destroyed device
    /// has no outstanding GPU work left to finish, so "done" is the honest answer to a straggler poll) and so
    /// <c>WaitForIdle</c> becomes a no-op. Reaching those through the device would make every wrapper hold the
    /// device, which is the reference cycle the incumbent deliberately does not have. The read surface is
    /// <see cref="IsAlive"/> and <see cref="IsDead"/> and nothing else.
    /// </para>
    /// <para>
    /// THE MEMORY MODEL IS THE WHOLE IMPLEMENTATION. The field is <c>volatile</c>, so a reader on another thread
    /// sees the flip without a lock, which is the point: disposal runs on whatever thread the consumer happens to
    /// be on, and taking the device's lifecycle lock there would deadlock against the very teardown that flipped
    /// this. There is deliberately no way to flip it back. A device that has been destroyed does not come back,
    /// and an un-kill would turn a stale wrapper into a call against freed memory.
    /// </para>
    /// <para>
    /// IT IS THE ONE IMPLEMENTATION OF <see cref="ID3D11DeviceLiveness"/>, which is the read surface the fence
    /// work was built against while this latch did not exist yet. That interface is deliberately the read half
    /// only: the fence subsystem asks whether the device is dead and has no business flipping it, so
    /// <see cref="MarkDead"/> stays off it and the device's teardown remains the only caller.
    /// </para>
    /// </summary>
    internal sealed class D3D11DeviceLiveness : ID3D11DeviceLiveness
    {
        volatile bool _dead;

        /// <summary>True while the real device is still alive and a native release is safe to make.</summary>
        internal bool IsAlive => !_dead;

        /// <summary>True once <see cref="MarkDead"/> has run. Every native release must be skipped from here on,
        /// a fence reads as signaled, and a drain does nothing. Public because it implements
        /// <see cref="ID3D11DeviceLiveness"/>, which is no wider than the class: the class is internal.</summary>
        public bool IsDead => _dead;

        /// <summary>
        /// Flip the token, permanently. Called by the device's own teardown, inside its lifecycle lock and BEFORE
        /// the real device is released, so no wrapper can observe "alive" after the object it would release has
        /// gone. Idempotent, and safe to call from any thread.
        /// </summary>
        internal void MarkDead() => _dead = true;
    }
}
