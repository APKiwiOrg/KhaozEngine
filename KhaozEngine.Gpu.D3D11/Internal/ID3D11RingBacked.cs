namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// WHAT A BUFFER ANSWERS WHEN THE WRITE PATH ASKS WHERE ITS BYTES GO: a ring for a uniform buffer, and null
    /// for everything else. The second internal capability seam in this package after
    /// <see cref="ID3D11PipelineState"/>, and it exists for the same two reasons.
    /// <para>
    /// FIRST, IT KEEPS THE ROUTING OFF WINDOWS. <see cref="D3D11Buffer"/> is <c>[SupportedOSPlatform("windows")]</c>
    /// at the type level, so an emitter that named it directly could not be compiled into a body that runs
    /// everywhere and could not be exercised by a device-free test. This interface names no Direct3D type, so the
    /// rule "a uniform write goes to the ring and a bulk write goes to the arena" is one line in the emitter and a
    /// plain <c>[Fact]</c> in the suite.
    /// </para>
    /// <para>
    /// SECOND, IT MAKES THE ROUTING ONE DECISION RATHER THAN A CONVENTION. Every write path in the backend asks
    /// the same question of the same member: the deferred driver's encoder, the immediate driver's real emitter,
    /// and the device-level off-timeline write. A buffer that is ring-backed is never written any other way, and a
    /// buffer that is not never touches a ring, which is decision U4's split expressed as a type rather than as a
    /// rule someone has to remember at three call sites.
    /// </para>
    /// </summary>
    internal interface ID3D11RingBacked
    {
        /// <summary>
        /// This buffer's constant-buffer ring, or null when it has none. Non-null for exactly the
        /// <see cref="GpuBufferUsage.UniformBuffer"/> buffers (decision U1), which the creation-time invariant of
        /// decision U3 makes an exclusive set: a uniform buffer combined with any other bindable usage is refused
        /// at creation, so a ring-backed buffer can never also be a vertex, index, indirect or structured buffer
        /// whose bind would address segment zero while its uniform bind addressed segment N.
        /// </summary>
        D3D11UniformRing? Ring { get; }
    }
}
