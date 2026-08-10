using System;
using System.Globalization;

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
    /// is padded up, which is the <c>(4 - size % 4) % 4</c> the incumbent already applies on its own aligned
    /// path. The OFFSET half THROWS by name. The incumbent instead routes any unaligned copy through an embedded
    /// compute shader driven by a dedicated compute pipeline, and shipping a second metallib plus a second
    /// pipeline for a case no consumer produces is the unreachable-code reproduction G1 declined once already.
    /// </para>
    ///
    /// <para><b>THE PAD LANDS INSIDE THE ALLOCATION, AND THE PROOF IS ARITHMETIC RATHER THAN A BOUND CHECK.</b>
    /// Every buffer is allocated at <c>MetalBufferPolicy.AllocationBytes</c>, which is its logical size rounded up
    /// to four. An offset that reaches here is a multiple of four and <c>offset + size</c> is inside the LOGICAL
    /// size, so <c>offset + align4(size)</c> is <c>align4(offset + size)</c>, which is at most
    /// <c>align4(logical)</c>, which is what was allocated. That holds on the destination and on the source
    /// alike, so the padded copy never reads or writes past an allocation on either side.</para>
    ///
    /// <para><b>THE THROW IS THE FOLLOW-UP's TRIGGER.</b> Section 9.3 files the unaligned-copy support with this
    /// refusal as the thing that would ask for it, and the device-free test over every <c>CopyBuffer</c> CALL
    /// SITE in the engine is what says nothing legitimate reaches it today.</para>
    /// </summary>
    internal static class MetalCopyAlignment
    {
        /// <summary>The multiple macOS requires of both offsets and of the size.</summary>
        internal const ulong Bytes = MetalStagingArena.CopyAlignment;

        /// <summary>Whether <paramref name="offsetBytes"/> can be passed to the copy selector as it stands.
        /// </summary>
        internal static bool IsAligned(ulong offsetBytes) => offsetBytes % Bytes == 0;

        /// <summary>
        /// The number of bytes the copy actually moves for a payload of <paramref name="sizeBytes"/>: the size
        /// rounded UP, which is the half of the ruling that pads rather than throws.
        /// </summary>
        internal static uint PaddedSize(uint sizeBytes) => MetalStagingArena.AlignedCopyBytes(sizeBytes);

        /// <summary>
        /// Refuse an offset the copy selector cannot take, naming which side of which command it came from.
        /// </summary>
        /// <param name="offsetBytes">The caller's offset.</param>
        /// <param name="parameterName">The entry point's own parameter name, for the exception.</param>
        /// <param name="what">What the caller was doing, as a sentence opener ("A native Metal buffer copy").
        /// </param>
        /// <param name="side">Which end of the copy the offset belongs to ("source" or "destination").</param>
        /// <exception cref="ArgumentOutOfRangeException">The offset is not a multiple of
        /// <see cref="Bytes"/>.</exception>
        internal static void RequireAlignedOffset(ulong offsetBytes, string parameterName, string what,
            string side)
        {
            if (IsAligned(offsetBytes)) return;

            throw new ArgumentOutOfRangeException(parameterName, offsetBytes,
                what + " was given a " + side + " offset of "
                + offsetBytes.ToString(CultureInfo.InvariantCulture) + ", which is not a multiple of "
                + Bytes.ToString(CultureInfo.InvariantCulture)
                + ". copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size: requires that of both offsets "
                + "on macOS. The incumbent routes the unaligned case through an embedded compute shader and a "
                + "dedicated compute pipeline, which this backend declines to reproduce for a case no shipped "
                + "call site produces (section 9.3). Align the offset, or write the buffer through the "
                + "device-level UpdateBuffer instead, which is a plain copy with no blit behind it.");
        }
    }
}
