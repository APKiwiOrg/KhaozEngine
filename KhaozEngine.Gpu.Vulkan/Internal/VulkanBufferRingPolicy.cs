using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// WHICH BUFFERS ARE RING-BACKED, AND THE COMBINATION THAT IS REFUSED AT CREATION. Decision V-M7, section 9.2.
    /// Two invariants, both adopted verbatim from the Direct3D 11 backend's U3 because the POLICY is identical even
    /// where the mechanism is not.
    ///
    /// <para><b>FIRST: ONLY <see cref="GpuBufferUsage.UniformBuffer"/> USAGE IS RING-BACKED.</b> A storage buffer's
    /// descriptor names the whole allocation, so it would address segment zero forever while a uniform read
    /// addressed segment N. Nothing else is cut into segments at all.</para>
    ///
    /// <para><b>SECOND: A RING-BACKED BUFFER NEVER RECEIVES A NON-UNIFORM BINDING.</b> A buffer created
    /// <c>UniformBuffer | StructuredBufferReadOnly</c>, with either read-write structured bit, or with the vertex,
    /// index or indirect bits, throws HERE rather than rendering one frame's data as another's. Only the DYNAMIC
    /// UNIFORM descriptor carries the per-frame base, as its <c>pDynamicOffsets</c> entry (V-D4). A vertex bind, an
    /// index bind, an indirect argument read and a storage descriptor all address byte zero of the buffer, so each
    /// of them would read the first segment while the uniform read read the current one. Nothing about that is an
    /// error at run time: it is a frame of data being read as another frame's, with nothing thrown and nothing
    /// logged.</para>
    ///
    /// <para><b>AND THAT MAKES IT A BACKEND-DIVERGENT CREATION FAILURE, WHICH IS THE PART THAT MATTERS MOST.</b>
    /// The combination is legal on the seam and is ACCEPTED by <see cref="GpuBackendKind.Vulkan"/>, the Veldrid
    /// leg. It is refused here. That divergence is DOCUMENTED (the package README's ring section and
    /// <c>docs/USING-KHAOZENGINE.md</c>) rather than left for a consumer to discover, which is V-M7's own emphasis.
    /// The combination is vacuous in the engine today, so nothing legitimate reaches the throw, but it is
    /// expressible on the seam and refusing it silently would be worse than refusing it loudly.</para>
    ///
    /// <para><b>THE HOOK ROW 9 CALLS.</b> Buffer creation is
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/519">row 9</see>'s
    /// <c>IGpuResourceFactory.CreateBuffer</c>. This type is the check <see cref="VulkanBuffer"/>'s constructor
    /// makes as its FIRST statement, before it allocates anything: <see cref="ForBuffer"/> either throws or
    /// answers whether to build a <see cref="VulkanUniformRing"/> for the buffer and size the <c>VkBuffer</c> at
    /// <see cref="VulkanRingStride.TotalBytesFor"/> instead of the caller's own size. It is a static function over
    /// a flags word rather than a method on the factory precisely so it could be complete and tested one row
    /// early.
    /// </para>
    /// </summary>
    internal static class VulkanBufferRingPolicy
    {
        /// <summary>The bits that describe HOW a buffer's bytes are bound, as opposed to where its memory lives.
        /// <see cref="GpuBufferUsage.Dynamic"/> and <see cref="GpuBufferUsage.Staging"/> are deliberately not
        /// here.</summary>
        const GpuBufferUsage BindingBits =
            GpuBufferUsage.VertexBuffer
            | GpuBufferUsage.IndexBuffer
            | GpuBufferUsage.UniformBuffer
            | GpuBufferUsage.StructuredBufferReadOnly
            | GpuBufferUsage.StructuredBufferReadWrite
            | GpuBufferUsage.IndirectBuffer;

        /// <summary>
        /// Whether a buffer of <paramref name="usage"/> is ring-backed, refusing the combination V-M7 forbids.
        /// </summary>
        /// <param name="usage">The description's usage flags, exactly as the seam's caller wrote them.</param>
        /// <returns>True for a uniform buffer, which is cut into <see cref="VulkanFramesInFlight"/> segments.
        /// False for everything else, whose <c>VkBuffer</c> is the caller's own size.</returns>
        /// <exception cref="ArgumentException">The uniform bit is combined with any other binding bit. See the
        /// class note: this is a documented backend divergence, not a bug.</exception>
        internal static bool ForBuffer(GpuBufferUsage usage)
        {
            if ((usage & GpuBufferUsage.UniformBuffer) == 0) return false;

            GpuBufferUsage others = usage & BindingBits & ~GpuBufferUsage.UniformBuffer;
            if (others == GpuBufferUsage.None) return true;

            throw new ArgumentException(
                $"A buffer was created as GpuBufferUsage.UniformBuffer | {others} on the native Vulkan backend, "
                + "which refuses that combination at creation. A uniform buffer here is RING-BACKED: it holds one "
                + "segment per frame in flight and the frame's base offset is supplied at the bind, as the dynamic "
                + "uniform descriptor's pDynamicOffsets entry. No other binding carries that base, so the "
                + $"{others} bind would address the first segment while the uniform bind addressed the current "
                + "one, and one frame's data would be read as another's with nothing thrown. This combination IS "
                + "accepted by GpuBackendKind.Vulkan (the Veldrid leg), so it is a documented divergence of "
                + "GpuBackendKind.VulkanNative rather than a defect. Create two buffers instead.",
                nameof(usage));
        }

        /// <summary>
        /// The same question WITHOUT the throw, for a caller that is auditing a usage rather than creating a
        /// buffer. A refused combination answers false here, because it is not ring-backed either.
        /// </summary>
        internal static bool IsRingBacked(GpuBufferUsage usage)
            => (usage & GpuBufferUsage.UniformBuffer) != 0
                && (usage & BindingBits & ~GpuBufferUsage.UniformBuffer) == GpuBufferUsage.None;

        /// <summary>Whether <paramref name="usage"/> is the combination <see cref="ForBuffer"/> refuses. Present so
        /// a diagnostic and a test can name the divergence without provoking the exception.</summary>
        internal static bool IsRefusedCombination(GpuBufferUsage usage)
            => (usage & GpuBufferUsage.UniformBuffer) != 0
                && (usage & BindingBits & ~GpuBufferUsage.UniformBuffer) != GpuBufferUsage.None;
    }
}
