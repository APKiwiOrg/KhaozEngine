using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// SECTION 9.3's <c>CopyBuffer</c> ALIGNMENT RULING, IN ONE PLACE, because two seam members now need it and
    /// the second copy of a rule is the one that drifts. Row 8
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/574) wrote it for the record-time bulk
    /// <c>UpdateBuffer</c> and row 14 (https://github.com/APKiwiOrg/KhaozEngine/issues/580) needs the identical
    /// rule for <c>IGpuCommandList.CopyBuffer</c> and for the staging-to-staging arm of a texture copy.
    ///
    /// <para><b>THE RULING IS ASYMMETRIC AND BOTH HALVES ARE DELIBERATE.</b> macOS requires the source offset,
    /// the destination offset AND the size of
    /// <c>copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:</c> to be multiples of four. The SIZE half
    /// is padded up, which is the <c>(4 - size % 4) % 4</c> the incumbent already applied on its own aligned
    /// path. The OFFSET half THROWS by name. The incumbent instead routed any unaligned copy through an embedded
    /// compute shader driven by a dedicated compute pipeline, and shipping a second metallib plus a second
    /// pipeline for a case no consumer produces is the unreachable-code reproduction G1 declined once already.
    /// </para>
    ///
    /// <para><b>THE PAD LANDS INSIDE THE ALLOCATION OF AN <c>IGpuBuffer</c>, AND THE PROOF IS ARITHMETIC RATHER
    /// THAN A BOUND CHECK.</b> A plain buffer is allocated at <c>MetalBufferPolicy.AllocationBytes</c>, which is
    /// its logical size rounded up to four. An offset that reaches here is a multiple of four and
    /// <c>offset + size</c> is inside the LOGICAL size, so <c>offset + align4(size)</c> is
    /// <c>align4(offset + size)</c>, which is at most <c>align4(logical)</c>, which is what was allocated. A
    /// RING-BACKED buffer does not take that number at all (<c>MetalBuffer.RingAllocationBytes</c> gives it
    /// <c>MetalRingStride.SegmentStrideFor</c> times the frame count), and the pad is safe there for a stronger
    /// reason rather than the same one: a segment stride is rounded up to 256, which subsumes the rounding to four
    /// rather than stacking with it. Either way it holds on the destination and on the source alike, so a padded
    /// copy between two <c>IGpuBuffer</c>s never reads or writes past an allocation on either side.</para>
    ///
    /// <para><b>AND IT IS NOT APPLIED TO A COPY BETWEEN TWO STAGING TEXTURES, WHICH IS THE ONE PLACE THE PROOF
    /// ABOVE DOES NOT REACH.</b> Neither of those buffers is a <c>MetalBuffer</c>: a staging texture allocates its
    /// <c>MTLBuffer</c> at exactly <c>MetalStagingLayout.TotalBytes</c>, with the subresources PACKED end to end
    /// and no rounding anywhere, so a pad on the last subresource runs past the allocation and a pad on any other
    /// one overwrites the first bytes of the next. <c>MetalTransferPlan.StagingToStaging</c> carries the rule and
    /// the incumbent's own shape backs it: its staging-to-staging arm issues EXACT row-sized copies with no
    /// padding at all.</para>
    ///
    /// <para><b>THE OFFSET HALF IS THE SEAM'S RULE NOW, AND THIS TYPE FORWARDS TO IT (17.40.0,
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/684">#684</see>).</b> The refusal used to be
    /// this backend's alone, so the same public call succeeded on Veldrid, native Vulkan and native Direct3D 11
    /// and threw here (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/602">#602</see>). All four
    /// enforce it now, with one wording, out of <see cref="GpuCopyAlignment"/>. What stayed local is the SIZE
    /// half: only Metal needs the size aligned, it pads rather than refusing, and the pad's safety proof above is
    /// about this backend's own allocations.</para>
    ///
    /// <para><b>THE THROW IS THE FOLLOW-UP's TRIGGER.</b> Section 9.3 files the unaligned-copy support with this
    /// refusal as the thing that would ask for it, and the device-free test over every <c>CopyBuffer</c> CALL
    /// SITE in the engine is what says nothing legitimate reaches it today.</para>
    /// </summary>
    internal static class MetalCopyAlignment
    {
        /// <summary>The multiple macOS requires of both offsets and of the size. The offset half of that is the
        /// seam's <see cref="GpuCopyAlignment.Bytes"/>, the same four, and
        /// <c>CopyBufferOffsetContractTests</c> pins the two together.</summary>
        internal const ulong Bytes = MetalStagingArena.CopyAlignment;

        /// <summary>Whether <paramref name="offsetBytes"/> can be passed to the copy selector as it stands.
        /// </summary>
        internal static bool IsAligned(ulong offsetBytes) => GpuCopyAlignment.IsAligned(offsetBytes);

        /// <summary>
        /// The number of bytes the copy actually moves for a payload of <paramref name="sizeBytes"/>: the size
        /// rounded UP, which is the half of the ruling that pads rather than throws.
        /// </summary>
        internal static uint PaddedSize(uint sizeBytes) => MetalStagingArena.AlignedCopyBytes(sizeBytes);

        /// <summary>
        /// Refuse an offset the copy selector cannot take, naming which side of which command it came from.
        /// THE SEAM'S RULE AND THE SEAM'S WORDING (<see cref="GpuCopyAlignment.RequireAlignedOffset"/>), so a
        /// caller who hits this on macOS reads the same sentence a caller hits on the other three backends.
        /// </summary>
        /// <param name="offsetBytes">The caller's offset.</param>
        /// <param name="parameterName">The entry point's own parameter name, for the exception.</param>
        /// <param name="what">What the caller was doing, as a sentence opener ("A native Metal buffer copy").
        /// </param>
        /// <param name="side">Which end of the copy the offset belongs to ("source" or "destination").</param>
        /// <exception cref="System.ArgumentOutOfRangeException">The offset is not a multiple of
        /// <see cref="Bytes"/>.</exception>
        internal static void RequireAlignedOffset(ulong offsetBytes, string parameterName, string what,
            string side)
            => GpuCopyAlignment.RequireAlignedOffset(offsetBytes, parameterName, what, side);
    }
}
