using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// THE SEAM'S <c>CopyBuffer</c> OFFSET RULE, IN ONE PLACE, SHARED BY ALL FOUR BACKENDS
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/602">#602</see>,
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/684">#684</see>). Both offsets handed to
    /// <see cref="IGpuCommandList.CopyBuffer"/> must be multiples of <see cref="Bytes"/>, and an offset that is
    /// not is refused by name, identically, on every backend.
    ///
    /// <para><b>THE RULE IS THE SEAM'S NOW RATHER THAN ONE BACKEND'S, AND THAT IS THE WHOLE POINT.</b> macOS
    /// requires both offsets and the size of
    /// <c>copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:</c> to be multiples of four, so the native
    /// Metal backend refused an unaligned offset from the day it shipped while Veldrid, native Vulkan and native
    /// Direct3D 11 all took one. The same public call therefore succeeded on three backends and threw on the
    /// fourth, which is a portability trap a consumer only finds on a user's Mac. Stating the strictest backend's
    /// requirement as the seam's contract is what makes the four agree, and it is the direction that cannot
    /// silently return the wrong bytes: rounding an offset UP moves which data the caller reads, and rounding it
    /// DOWN turns a copy into a read of a wider window that can run off the end of the source.
    /// </para>
    ///
    /// <para><b>THE SIZE IS NOT ITS BUSINESS.</b> Only Metal needs the size aligned, it pads the size up rather
    /// than refusing it (<c>MetalCopyAlignment.PaddedSize</c>, over an allocation rounded up to the same four),
    /// and a pad moves no data the caller asked for. So the size stays unconstrained at the seam and this type
    /// covers offsets alone.</para>
    ///
    /// <para><b>INTERNAL, AND VISIBLE TO THE THREE NATIVE BACKENDS ACROSS <c>InternalsVisibleTo</c>,</b> which is
    /// what lets one rule and one wording serve four implementations instead of four copies that drift. The
    /// public statement of the same rule is the XML doc on <see cref="IGpuCommandList.CopyBuffer"/> and on
    /// <see cref="GpuReadback.ReadBuffer{T}"/>, which is where a consumer reads it.</para>
    /// </summary>
    internal static class GpuCopyAlignment
    {
        /// <summary>The multiple the seam requires of both copy offsets, which is what macOS requires of
        /// them.</summary>
        internal const uint Bytes = 4;

        /// <summary>Whether <paramref name="offsetBytes"/> can be handed to a backend copy as it stands.</summary>
        internal static bool IsAligned(ulong offsetBytes) => offsetBytes % Bytes == 0;

        /// <summary>
        /// Refuse an offset the seam does not accept, naming which side of which command it came from.
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
                + ". IGpuCommandList.CopyBuffer requires that of both of its offsets on every backend, because "
                + "copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size: requires it on macOS and a seam "
                + "member that succeeds on three backends and throws on the fourth is a trap a consumer finds on "
                + "a user's machine (#602). Align the offset, or write the buffer through the device-level "
                + "UpdateBuffer instead, which is a plain copy with no blit behind it.");
        }
    }
}
