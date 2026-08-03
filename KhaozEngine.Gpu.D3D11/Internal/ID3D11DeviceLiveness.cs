namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// DECISION X3, as a hook the fence subsystem can be built against before the device exists: after the device
    /// is dead, disposal is a no-op, <see cref="IGpuFence.Signaled"/> reads TRUE and <c>WaitForIdle</c> is a
    /// no-op. All three say the same thing from different angles, which is that a destroyed device has no
    /// outstanding GPU work left to finish, so the honest answer to "is it done" is yes and the honest answer to
    /// "wait for it" is to return.
    /// <para>
    /// GETTING THAT WRONG IS NOT A COSMETIC BUG. A fence that read UNSIGNALLED after device death would strand
    /// <c>RetiredResourcePool</c> forever on a batch it can never free, and a drain that spun would spin on a
    /// counter nothing can advance any more. Both are teardown-order hazards, and teardown order is exactly where
    /// a resource wrapper outliving its device is normal rather than a defect.
    /// </para>
    /// <para>
    /// AN INTERFACE RATHER THAN THE DEVICE ITSELF, for two reasons that both outlast the row that built it. It
    /// keeps the whole X3 behaviour headless-testable (a test flips death by hand and asserts what every member
    /// then answers, on any operating system), and it lets this subsystem be built and merged before the device's
    /// real liveness latch exists. The latch is the resources row's work, and it becomes the one implementation
    /// of this interface. The incumbent's equivalent is <c>KhaozEngine.Gpu.Internal.DeviceLiveness</c>, a shared
    /// mutable token every resource wrapper reads, and the native device's is expected to have the same shape.
    /// </para>
    /// </summary>
    internal interface ID3D11DeviceLiveness
    {
        /// <summary>True once the device has been destroyed. Read lock-free on every fence poll and at the top of
        /// every drain, so an implementation must be cheap and must never take a lock.</summary>
        bool IsDead { get; }
    }

    /// <summary>The default when no liveness token has been wired in yet: the device is alive and stays alive.
    /// <para>This is the SAFE default rather than a convenient one. Defaulting to dead would make every fence
    /// read signalled and every drain a no-op, which is the failure X3 exists to produce only after death and is
    /// silent before it: a pool would free resources the GPU is still reading and the corruption would surface
    /// somewhere else entirely.</para></summary>
    internal sealed class D3D11LiveDevice : ID3D11DeviceLiveness
    {
        /// <summary>The shared instance. Stateless, so one is enough for the process.</summary>
        internal static readonly D3D11LiveDevice Instance = new();

        D3D11LiveDevice() { }

        /// <inheritdoc/>
        public bool IsDead => false;
    }
}
