namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// WHICH OF METAL'S THREE ENCODER KINDS IS OPEN, and <see cref="None"/> for the state between them.
    /// <para>
    /// EXACTLY ONE IS OPEN AT A TIME, which is Metal's own rule rather than a policy this design invents: a
    /// command buffer hands out one encoder, and a second cannot be created until the first has been sent
    /// <c>-endEncoding</c>. <see cref="MetalEncoderScope"/> is the one type that owns the transitions, and this
    /// enum is what it is a state machine over.
    /// </para>
    /// <para>
    /// THERE IS NO PARALLEL RENDER COMMAND ENCODER HERE (section 6.5). The GPU seam has no sub-list concept and
    /// multi-threaded recording is not a shipped feature, so a fourth member would name a kind nothing can reach.
    /// </para>
    /// </summary>
    internal enum MetalEncoderKind
    {
        /// <summary>No encoder is open. What a fresh command buffer starts at, and what every
        /// <c>EnsureNo</c> transition ends at.</summary>
        None = 0,

        /// <summary><c>MTLRenderCommandEncoder</c>, opened from an <c>MTLRenderPassDescriptor</c> (M-A1's
        /// deferred begin, row 12: https://github.com/APKiwiOrg/KhaozEngine/issues/578).</summary>
        Render = 1,

        /// <summary><c>MTLBlitCommandEncoder</c>: copies, mip generation and the record-time staged upload. The
        /// kind whose cost 2.1 is about, because opening one ENDS a render encoder and discards every piece of
        /// encoder-scoped state with it (M-R4).</summary>
        Blit = 2,

        /// <summary><c>MTLComputeCommandEncoder</c>, opened with the SERIAL dispatch type (M-H4, row 14:
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/580).</summary>
        Compute = 3,
    }
}
