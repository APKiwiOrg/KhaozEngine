using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// WHAT A BUFFER IS AT CREATION: its real allocation size, and the one usage combination this backend refuses.
    ///
    /// <para><b>THERE IS NO USAGE MAP HERE, AND THAT IS THE WHOLE OF M-M1 AND M-M2 IN ONE SENTENCE.</b> The Vulkan
    /// sibling derives seven <c>VkBufferUsageFlags</c> bits and a memory ladder from the seam's usage, and
    /// Direct3D 11 derives bind flags and a CPU access mode.
    /// Metal's <c>-newBufferWithLength:options:</c> takes a length and a
    /// storage mode and nothing else, so a buffer here has no declared use at all: what it is used for is decided
    /// entirely by where it gets bound. The incumbent passes a literal <c>0</c> for the options of every buffer it
    /// creates, which is Shared storage with the default cache mode, and this backend passes the same value
    /// spelled out (<c>MTLResourceOptions.SharedDefaultCache</c>).</para>
    ///
    /// <para><b>THE RING IS NOT BUILT HERE AND THE PREDICATE IT WILL READ IS.</b>
    /// <see cref="IsRingBacked"/> is the creation-time question M-M6's two invariants are stated in terms of, and
    /// the uniform ring itself (one buffer of <c>stride * FramesInFlight</c>, the segment gate, the bind-time
    /// base) is the ring row's, https://github.com/APKiwiOrg/KhaozEngine/issues/574. A uniform buffer created
    /// today is a plain Shared buffer of the size the caller asked for, and that row is what changes its SIZE. The
    /// refusal below lives here rather than there because it is a CREATION failure and creation is this row's, and
    /// because a consumer that could create the refused shape today would have it start throwing later.</para>
    /// </summary>
    internal static class MetalBufferPolicy
    {
        /// <summary>
        /// The real allocation size for a buffer the seam asked <paramref name="sizeInBytes"/> bytes for:
        /// <c>size + (4 - size % 4) % 4</c>, which is <c>MTLBuffer.ActualCapacity</c> reproduced exactly.
        /// <para>
        /// THE ROUNDING IS REPRODUCED RATHER THAN DROPPED, and the reason is a copy rather than an allocation.
        /// Section 9.3 keeps the size-rounding half of the incumbent's <c>CopyBuffer</c> alignment handling: a
        /// copy whose SIZE is not a multiple of 4 is padded up on the aligned path, and it can only be padded up
        /// into bytes the destination buffer really owns. A buffer allocated at its exact requested size would
        /// make that pad a write past the end. The engine creates plenty of buffers whose size is not a multiple
        /// of 4 (a byte-per-texel upload staging buffer is the obvious one), so this is reached rather than
        /// theoretical.
        /// </para>
        /// <para>
        /// <see cref="IGpuBuffer.SizeInBytes"/> STILL REPORTS THE REQUESTED SIZE, which is also the incumbent's
        /// split: <c>MTLBuffer.SizeInBytes</c> is what was asked for and <c>ActualCapacity</c> is what was
        /// allocated. A caller that saw the rounded size would compute a different element count from the same
        /// buffer on this backend than on the other two.
        /// </para>
        /// </summary>
        internal static uint AllocationBytes(uint sizeInBytes) => sizeInBytes + ((4 - (sizeInBytes % 4)) % 4);

        /// <summary>
        /// Whether a buffer of <paramref name="usage"/> is backed by the uniform ring. U3's first creation-time
        /// invariant, adopted verbatim (M-M6): ONLY <see cref="GpuBufferUsage.UniformBuffer"/> usage is
        /// ring-backed, so a structured buffer's own binding stays correct.
        /// </summary>
        internal static bool IsRingBacked(GpuBufferUsage usage) => (usage & GpuBufferUsage.UniformBuffer) != 0;

        /// <summary>
        /// U3's SECOND creation-time invariant (M-M6): a ring-backed buffer that ALSO declares a structured
        /// binding throws at creation.
        ///
        /// <para><b>THIS IS A DOCUMENTED BACKEND-DIVERGENT CREATION FAILURE rather than a defect.</b> The
        /// combination is legal on the seam and both Veldrid backends accept it, and it is vacuous in this engine
        /// today: nothing creates a buffer with a uniform bit and a structured bit at once. The ring rebases every
        /// bind of a ring-backed buffer by a per-frame offset, and a structured binding of the same buffer would
        /// read whichever segment the frame happened to land on. Refusing at creation is what turns that into a
        /// message at the call site instead of a wrong buffer read three subsystems away. The package README
        /// records it, which is the point of the decision: it should be found in the documentation rather than
        /// discovered by a consumer.</para>
        /// </summary>
        /// <exception cref="ArgumentException">The usage is ring-backed and declares a structured binding.</exception>
        internal static void RequireCreatable(GpuBufferUsage usage)
        {
            const GpuBufferUsage Structured =
                GpuBufferUsage.StructuredBufferReadOnly | GpuBufferUsage.StructuredBufferReadWrite;

            if (!IsRingBacked(usage) || (usage & Structured) == 0) return;

            throw new ArgumentException(
                "The native Metal backend cannot create a buffer that is both a uniform buffer and a structured "
                + "buffer (asked for "
                + usage.ToString()
                + "). A uniform buffer here is backed by the frame ring, which rebases every bind of it by a "
                + "per-frame offset, and a structured binding of the same buffer would read whichever segment "
                + "that frame happened to land on. Both Veldrid backends accept this combination and nothing in "
                + "this engine creates it, so it is a documented backend-divergent creation failure rather than "
                + "a gap: create two buffers.",
                nameof(usage));
        }

        /// <summary>
        /// Refuse a write that runs off the end of a buffer, by name and with both numbers. The incumbent's
        /// <c>UpdateBufferCore</c> is an unguarded <c>Unsafe.CopyBlock</c> into <c>contents()</c>, so the same
        /// mistake there is a silent write into whatever the allocator put next.
        /// </summary>
        internal static void RequireWriteFits(uint offsetBytes, uint writeBytes, uint sizeInBytes)
        {
            if ((ulong)offsetBytes + writeBytes <= sizeInBytes) return;

            throw new ArgumentOutOfRangeException(nameof(writeBytes), writeBytes,
                "A native Metal buffer write of "
                + writeBytes.ToString(CultureInfo.InvariantCulture)
                + " bytes at offset "
                + offsetBytes.ToString(CultureInfo.InvariantCulture)
                + " runs past the end of a buffer of "
                + sizeInBytes.ToString(CultureInfo.InvariantCulture)
                + " bytes. The incumbent copies into contents() with no bound check at all, so the same call "
                + "there overwrites whatever the driver placed after this allocation.");
        }
    }
}
