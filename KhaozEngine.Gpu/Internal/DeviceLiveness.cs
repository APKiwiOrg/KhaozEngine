namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// THE READ HALF of the "is the device still there" token, shared by every GPU backend in the engine: the
    /// Veldrid wrappers here, and the three native backends (decision X3 for Direct3D 11, V-F10 for Vulkan, M-F6
    /// for Metal, all the same decision under three names). After the device is dead, disposal is a no-op,
    /// <c>IGpuFence.Signaled</c> reads TRUE and <c>WaitForIdle</c> is a no-op. All three say the same thing from
    /// different angles, which is that a destroyed device has no outstanding GPU work left to finish, so the
    /// honest answer to "is it done" is yes and the honest answer to "wait for it" is to return.
    /// <para>
    /// GETTING IT WRONG IS NOT COSMETIC. A fence that read UNSIGNALLED after device death would strand a retire
    /// pool forever on a batch it can never free, and a drain that spun would spin on a counter nothing can
    /// advance any more. Both are teardown-order hazards, and teardown order is exactly where a resource wrapper
    /// outliving its device is normal rather than a defect.
    /// </para>
    /// <para>
    /// AN INTERFACE RATHER THAN THE DEVICE ITSELF, for a reason all four implementations gave independently:
    /// reaching the device would make every wrapper hold the device, which is the reference cycle none of them
    /// wants, and the interface keeps the whole behaviour headless-testable. A test flips death by hand and
    /// asserts what every member then answers, on any operating system.
    /// </para>
    /// <para>
    /// THE READ HALF ONLY, deliberately. A fence subsystem, a timeline or a staging arena asks whether the device
    /// is dead and has no business flipping it, so <see cref="DeviceLiveness.MarkDead"/> is not on this interface
    /// and only a device teardown or a device-loss latch calls it.
    /// </para>
    /// <para>
    /// IT IS ALSO A DEVICE IDENTITY TOKEN on the Metal backend, which is a second use the shared shape carries
    /// for free. Apple silicon reports one <c>MTLDevice</c> per process, so a handle comparison decides nothing
    /// and <c>MetalResourceOwnership.Require</c> compares this reference instead. What that gate needs is a
    /// reference-identity token, and an interface is exactly as good a one as a per-backend class was.
    /// </para>
    /// </summary>
    internal interface IDeviceLiveness
    {
        /// <summary>True once the device has been destroyed, torn down or lost. Read lock-free on every fence
        /// poll and at the top of every drain, so an implementation must be cheap and must never take a
        /// lock.</summary>
        bool IsDead { get; }
    }

    /// <summary>
    /// The shared "is the device still there" token. One instance per device, handed to every wrapper the device
    /// creates, flipped ONCE before the real device goes away, and read lock-free afterwards.
    /// <para>
    /// TWO THINGS FLIP IT, for the same underlying reason. The device's own teardown flips it inside its
    /// lifecycle lock, which is the ordinary case, and the backend's device-loss latch flips it when the driver
    /// gives up, which is the other way a device stops existing: the objects are already gone, nothing asked for
    /// it, and every release from that point on would be a release against freed memory.
    /// </para>
    /// <para>
    /// WHERE IN TEARDOWN THE FLIP HAPPENS IS THE CALLER'S DECISION AND IT DIFFERS PER BACKEND, which is why this
    /// type has no opinion about it. The Veldrid wrapper flips it FIRST, correctly, because destroying a Veldrid
    /// device already frees its children. Direct3D 11 flips it LAST, because every release above it reads the
    /// token and would otherwise be skipped, leaving an <c>ID3D11Device</c> alive holding a swapchain nobody can
    /// reach. Vulkan flips it between <c>vkDeviceWaitIdle</c> and <c>vkDestroyDevice</c>. Metal flips it after
    /// the drain and before the queue and the device are released.
    /// </para>
    /// <para>
    /// WHAT IT BUYS. Destroying a GPU device already frees every child object it made, so a wrapper disposed
    /// after its device would be releasing something that no longer exists. On the Veldrid Vulkan path that call
    /// ABORTED THE PROCESS through the loader rather than failing quietly, and the two teardown-order crashes it
    /// produced are why the token exists at all. Every wrapper's <c>Dispose</c> is therefore gated on
    /// <see cref="IsAlive"/>, which makes post-death disposal a no-op instead of a fault, and the gate is what
    /// lets a consumer keep the ordinary "dispose your resources whenever" contract.
    /// </para>
    /// <para>
    /// THE MEMORY MODEL IS THE WHOLE IMPLEMENTATION. The field is <c>volatile</c>, so a reader on another thread
    /// sees the flip without a lock, which is the point: disposal runs on whatever thread the consumer happens to
    /// be on, and taking the device's lifecycle lock there would deadlock against the very teardown that flipped
    /// this. Metal's completion handlers arrive on an arbitrary internal thread, so on that backend the latch's
    /// flip is a cross-thread write by construction rather than by accident. There is deliberately no way to flip
    /// it back. A device that has been destroyed does not come back, and an un-kill would turn a stale wrapper
    /// into a call against freed memory.
    /// </para>
    /// </summary>
    internal sealed class DeviceLiveness : IDeviceLiveness
    {
        volatile bool _dead;

        /// <summary>True while the real device is still alive and a native release is safe to make.</summary>
        internal bool IsAlive => !_dead;

        /// <summary>True once <see cref="MarkDead"/> has run. Every native release must be skipped from here on,
        /// a fence reads as signaled, and a drain does nothing. Public because it implements
        /// <see cref="IDeviceLiveness"/>, which is no wider than the class: the class is internal.</summary>
        public bool IsDead => _dead;

        /// <summary>
        /// Flip the token, permanently. Called by the device's own teardown, inside its lifecycle lock, and by
        /// the backend's device-loss latch the moment a loss is latched, which is the same statement arriving
        /// from the driver instead of from the application. Idempotent, and safe to call from any thread.
        /// </summary>
        internal void MarkDead() => _dead = true;
    }

    /// <summary>The default when no liveness token has been wired in yet: the device is alive and stays alive.
    /// <para>This is the SAFE default rather than a convenient one. Defaulting to dead would make every fence
    /// read signalled and every drain a no-op, which is the behaviour the token wants only AFTER death and which
    /// is silent before it: a pool would free resources the GPU is still reading and the corruption would surface
    /// somewhere else entirely.</para></summary>
    internal sealed class LiveDevice : IDeviceLiveness
    {
        /// <summary>The shared instance. Stateless, so one is enough for the process.</summary>
        internal static readonly LiveDevice Instance = new();

        LiveDevice() { }

        /// <inheritdoc/>
        public bool IsDead => false;
    }
}
